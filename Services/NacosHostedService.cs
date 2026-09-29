using System.Net;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// On start: registers this node with Nacos and begins periodic heartbeats.
/// On stop: deregisters from Nacos gracefully.
/// Runs only when clustering + Nacos are both enabled.
/// </summary>
public sealed class NacosHostedService : IHostedService
{
    private readonly ClusterRuntimeConfig _clusterConfig;
    private readonly ClusterSelf _self;
    private readonly NacosClient _nacos;
    private readonly ILogger<NacosHostedService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _heartbeatLoop;
    private string _ip = "";
    private int _port;

    public NacosHostedService(
        ClusterRuntimeConfig clusterConfig,
        ClusterSelf self,
        NacosClient nacos,
        ILogger<NacosHostedService> logger)
    {
        _clusterConfig = clusterConfig;
        _self = self;
        _nacos = nacos;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_clusterConfig.UseNacos)
            return;

        (_ip, _port) = ResolveIpAndPort();

        // Build instance metadata with this node's info.
        var meta = new Dictionary<string, string>
        {
            ["nodeId"] = _self.NodeId,
            ["role"] = _self.Role,
            ["ocrMode"] = _self.OcrMode,
            ["model"] = _self.Model,
            ["dpi"] = _self.Dpi.ToString(),
            ["capacity"] = _self.Capacity.ToString(),
            ["engineCount"] = _self.EngineCount.ToString(),
        };

        // Copy any user-supplied metadata, but don't overwrite the standard keys.
        if (_clusterConfig.Nacos?.Metadata is not null)
        {
            foreach ((string k, string v) in _clusterConfig.Nacos.Metadata)
            {
                meta.TryAdd(k, v);
            }
        }

        // Add the advertise URL so other nodes can reach us.
        if (!string.IsNullOrWhiteSpace(_self.AdvertiseUrl))
            meta["advertiseUrl"] = _self.AdvertiseUrl.TrimEnd('/');

        // Register with retry.
        bool registered = false;
        for (int attempt = 0; attempt < 5 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            registered = await _nacos.RegisterInstanceAsync(_ip, _port, _clusterConfig.Nacos!.Weight, meta, cancellationToken)
                .ConfigureAwait(false);
            if (registered)
                break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        if (!registered)
        {
            _logger.LogWarning(
                "Nacos register failed after retries; this node will not appear in Nacos discovery. " +
                "It can still poll the coordinator directly.");
        }

        // Start heartbeat loop.
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(meta, _cts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_clusterConfig.UseNacos)
            return;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_heartbeatLoop is not null)
        {
            try
            {
                await _heartbeatLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        // Deregister.
        if (_ip.Length > 0)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await _nacos.DeregisterInstanceAsync(_ip, _port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nacos deregister on shutdown failed (ignored)");
            }
        }
    }

    private async Task HeartbeatLoopAsync(Dictionary<string, string> metadata, CancellationToken ct)
    {
        int intervalMs = Math.Max(1000, (_clusterConfig.Nacos?.HeartbeatIntervalSeconds ?? 5) * 1000);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _nacos.SendHeartbeatAsync(_ip, _port, metadata, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private (string ip, int port) ResolveIpAndPort()
    {
        string advertise = (_self.AdvertiseUrl ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(advertise) &&
            Uri.TryCreate(advertise, UriKind.Absolute, out Uri? uri))
        {
            int port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            string host = uri.Host;
            // Resolve DNS hostname to an IP; fall back to the host string.
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                IPAddress? ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ipv4 is not null)
                    return (ipv4.ToString(), port);
                if (addrs.Length > 0)
                    return (addrs[0].ToString(), port);
            }
            catch
            {
                // DNS resolution failed; use host as-is.
            }

            return (host, port);
        }

        // Fallback: pick a local IP.
        try
        {
            string hostName = Dns.GetHostName();
            IPAddress[] addrs = Dns.GetHostAddresses(hostName);
            IPAddress? ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ipv4 is not null)
                return (ipv4.ToString(), 5000);
        }
        catch { }

        return ("127.0.0.1", 5000);
    }
}
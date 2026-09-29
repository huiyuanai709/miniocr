using System.Text;
using System.Text.Json;
using MiniOcr;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Talks to Nacos via its Open HTTP API.
/// Supports register, heartbeat, deregister, service discovery, and login.
/// Designed for Native AOT: no reflection, no dynamic dispatch.
/// </summary>
public sealed class NacosClient
{
    private readonly HttpClient _http;
    private readonly string _serverAddr;
    private readonly string? _namespaceId;
    private readonly string _serviceName;
    private readonly string _groupName;
    private readonly string _clusterName;
    private readonly string? _accessToken;
    private readonly string? _username;
    private readonly string? _password;
    private readonly ILogger<NacosClient> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public NacosClient(
        HttpClient http,
        NacosFileConfig config,
        ILogger<NacosClient> logger)
    {
        _http = http;
        _serverAddr = (config.ServerAddr ?? "").Trim().TrimEnd('/');
        _namespaceId = string.IsNullOrWhiteSpace(config.Namespace) ? null : config.Namespace.Trim();
        _serviceName = string.IsNullOrWhiteSpace(config.ServiceName) ? "miniocr-cluster" : config.ServiceName.Trim();
        _groupName = string.IsNullOrWhiteSpace(config.GroupName) ? "DEFAULT_GROUP" : config.GroupName.Trim();
        _clusterName = string.IsNullOrWhiteSpace(config.ClusterName) ? "DEFAULT" : config.ClusterName.Trim();
        _accessToken = string.IsNullOrWhiteSpace(config.AccessToken) ? null : config.AccessToken.Trim();
        _username = string.IsNullOrWhiteSpace(config.Username) ? null : config.Username.Trim();
        _password = string.IsNullOrWhiteSpace(config.Password) ? null : config.Password.Trim();
        _logger = logger;
    }

    public string ServiceName => _serviceName;
    public string GroupName => _groupName;

    /// <summary>Whether authentication credentials were supplied.</summary>
    public bool HasAuth => !string.IsNullOrWhiteSpace(_accessToken) ||
                           (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password));

    // ---- Instance lifecycle ----

    public async Task<bool> RegisterInstanceAsync(
        string ip,
        int port,
        double weight,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        string meta = SerializeMetadata(metadata);
        var query = new QueryBuilder()
            .Add("ip", ip)
            .Add("port", port.ToString())
            .Add("serviceName", _serviceName)
            .Add("groupName", _groupName)
            .Add("clusterName", _clusterName)
            .Add("namespaceId", _namespaceId ?? "")
            .Add("weight", weight.ToString("F1"))
            .Add("healthy", "true")
            .Add("enabled", "true")
            .Add("ephemeral", "true")
            .AddIf("metadata", meta, !string.IsNullOrEmpty(meta))
            .AddIf("accessToken", token, !string.IsNullOrEmpty(token))
            .Build();

        string url = $"{_serverAddr}/nacos/v1/ns/instance?{query}";
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && body.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Nacos register: {ServiceName} {Ip}:{Port} ok", _serviceName, ip, port);
                return true;
            }

            _logger.LogWarning("Nacos register returned {Status}: {Body}", (int)resp.StatusCode, body.Truncate(200));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nacos register {Ip}:{Port} failed", ip, port);
            return false;
        }
    }

    public async Task<bool> SendHeartbeatAsync(
        string ip,
        int port,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        string meta = SerializeMetadata(metadata);
        var query = new QueryBuilder()
            .Add("serviceName", _serviceName)
            .Add("groupName", _groupName)
            .Add("ip", ip)
            .Add("port", port.ToString())
            .Add("namespaceId", _namespaceId ?? "")
            .Add("clusterName", _clusterName)
            .Add("ephemeral", "true")
            .AddIf("metadata", meta, !string.IsNullOrEmpty(meta))
            .AddIf("accessToken", token, !string.IsNullOrEmpty(token))
            .Build();

        string url = $"{_serverAddr}/nacos/v1/ns/instance/beat?{query}";
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Put, url);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Nacos heartbeat: {ServiceName} {Ip}:{Port} ok", _serviceName, ip, port);
                return true;
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Nacos heartbeat returned {Status}: {Body}", (int)resp.StatusCode, body.Truncate(200));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Nacos heartbeat {Ip}:{Port} failed", ip, port);
            return false;
        }
    }

    public async Task<bool> DeregisterInstanceAsync(string ip, int port, CancellationToken ct)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        var query = new QueryBuilder()
            .Add("ip", ip)
            .Add("port", port.ToString())
            .Add("serviceName", _serviceName)
            .Add("groupName", _groupName)
            .Add("namespaceId", _namespaceId ?? "")
            .Add("clusterName", _clusterName)
            .Add("ephemeral", "true")
            .AddIf("accessToken", token, !string.IsNullOrEmpty(token))
            .Build();

        string url = $"{_serverAddr}/nacos/v1/ns/instance?{query}";
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Delete, url);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Nacos deregister: {ServiceName} {Ip}:{Port} ok", _serviceName, ip, port);
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nacos deregister {Ip}:{Port} failed", ip, port);
            return false;
        }
    }

    // ---- Service discovery ----

    public async Task<List<NacosInstance>> ListInstancesAsync(CancellationToken ct)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        var query = new QueryBuilder()
            .Add("serviceName", _serviceName)
            .Add("groupName", _groupName)
            .Add("namespaceId", _namespaceId ?? "")
            .Add("clusters", _clusterName)
            .Add("healthyOnly", "true")
            .AddIf("accessToken", token, !string.IsNullOrEmpty(token))
            .Build();

        string url = $"{_serverAddr}/nacos/v1/ns/instance/list?{query}";
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            NacosInstanceListResponse? list = await JsonSerializer.DeserializeAsync(
                stream, AppJsonContext.Default.NacosInstanceListResponse, ct).ConfigureAwait(false);
            return list?.Hosts ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nacos list instances for {ServiceName} failed", _serviceName);
            return [];
        }
    }

    private static bool IsAbsoluteUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               Uri.TryCreate(url, UriKind.Absolute, out Uri? u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Resolves the full URL for a nacos instance, preferring <c>uri</c> from metadata,
    /// then <c>url</c>, then <c>advertiseUrl</c>, and falling back to <c>http://{ip}:{port}</c>.
    /// </summary>
    public static string ResolveUrl(NacosInstance instance)
    {
        if (instance.Metadata is not null)
        {
            if (instance.Metadata.TryGetValue("uri", out string? u) && IsAbsoluteUrl(u))
                return u.TrimEnd('/');
            if (instance.Metadata.TryGetValue("url", out u) && IsAbsoluteUrl(u))
                return u.TrimEnd('/');
            if (instance.Metadata.TryGetValue("advertiseUrl", out u) && IsAbsoluteUrl(u))
                return u.TrimEnd('/');
        }

        return $"http://{instance.Ip}:{instance.Port}";
    }

    // ---- Auth ----

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        // Use explicit access token if provided.
        if (!string.IsNullOrWhiteSpace(_accessToken))
            return _accessToken;

        // Use cached token if still fresh.
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            return null;

        // POST /nacos/v1/auth/login with form data.
        string url = $"{_serverAddr}/nacos/v1/auth/login";
        try
        {
            var form = new Dictionary<string, string>
            {
                ["username"] = _username,
                ["password"] = _password,
            };
            using HttpRequestMessage req = new(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            NacosLoginResponse? login = await JsonSerializer.DeserializeAsync(
                stream, AppJsonContext.Default.NacosLoginResponse, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(login?.AccessToken))
            {
                _cachedToken = login.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, login.TokenTtl - 30));
                return _cachedToken;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nacos login failed");
        }

        return null;
    }

    private static string SerializeMetadata(Dictionary<string, string>? meta)
    {
        if (meta is null || meta.Count == 0)
            return "";
        // Build JSON manually to avoid reflection; AOT-compatible.
        StringBuilder sb = new();
        sb.Append('{');
        bool first = true;
        foreach ((string k, string v) in meta)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"');
            sb.Append(EscapeJson(k));
            sb.Append("\":\"");
            sb.Append(EscapeJson(v));
            sb.Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        StringBuilder sb = new(s.Length + 4);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ---- Internal query-string helper ----

    private ref struct QueryBuilder
    {
        private readonly StringBuilder _sb;

        public QueryBuilder() => _sb = new StringBuilder();

        public QueryBuilder Add(string key, string value)
        {
            if (_sb.Length > 0)
                _sb.Append('&');
            _sb.Append(Uri.EscapeDataString(key));
            _sb.Append('=');
            _sb.Append(Uri.EscapeDataString(value));
            return this;
        }

        public QueryBuilder AddIf(string key, string value, bool condition)
        {
            if (condition)
                return Add(key, value);
            return this;
        }

        public string Build() => _sb.ToString();
    }
}

/// <summary>Extension to truncate a string to a max length for log safety.</summary>
internal static class StringExtensions
{
    public static string Truncate(this string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen];
}
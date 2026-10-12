using System.Net.Http.Headers;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Process-wide pool of LLM API keys. Each key has <see cref="PerKey"/> NER slots.
/// A NER chat completion acquires one slot and releases it when the HTTP call finishes.
/// Slots are handed out least-loaded, with round-robin as the tie break.
/// HTTP 401 disables that key for the rest of the process; the caller retries on another
/// live key when one remains. Key material is never written by this type — log <see cref="LlmKeyLease.Slot"/> only.
/// </summary>
public sealed class LlmKeyPool
{
    private readonly object _gate = new();
    private readonly string[] _keys;
    private readonly string[] _redactOrder;
    private readonly int[] _inUse;
    private readonly bool[] _disabled;
    private readonly LinkedList<TaskCompletionSource<int>> _waiters = new();
    private readonly int _perKey;
    private int _cursor;

    public LlmKeyPool(IReadOnlyList<string> keys, int perKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        if (keys is not null)
        {
            foreach (string key in keys)
                AddKey(kept, seen, key);
        }

        if (kept.Count > LlmRuntimeConfig.MaxApiKeys)
            kept.RemoveRange(LlmRuntimeConfig.MaxApiKeys, kept.Count - LlmRuntimeConfig.MaxApiKeys);

        _keys = kept.ToArray();
        _redactOrder = _keys.OrderByDescending(key => key.Length).ToArray();
        _inUse = new int[_keys.Length];
        _disabled = new bool[_keys.Length];
        // Unset or non-positive means the historical default of 8. A positive K is kept.
        _perKey = perKey <= 0 ? 8 : perKey;
    }

    /// <summary>
    /// Builds the pool from resolved settings. <see cref="LlmRuntimeConfig.MaxConcurrencyPerKey"/>
    /// wins when set; otherwise <see cref="LlmRuntimeConfig.MaxConcurrency"/> is the per-key limit
    /// (one key then matches the historical global cap; N keys allow about N times that).
    /// </summary>
    public static LlmKeyPool FromConfig(LlmRuntimeConfig config)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (config.ApiKeys is not null)
        {
            foreach (string key in config.ApiKeys)
                AddKey(keys, seen, key);
        }

        AddKey(keys, seen, config.ApiKey);
        int perKey = config.MaxConcurrencyPerKey > 0
            ? config.MaxConcurrencyPerKey
            : config.MaxConcurrency > 0 ? config.MaxConcurrency : 8;
        return new LlmKeyPool(keys, perKey);
    }

    public int KeyCount => _keys.Length;
    public int PerKey => _perKey;

    public int LiveCount
    {
        get
        {
            lock (_gate)
                return LiveCountLocked();
        }
    }

    /// <summary>Slots that can be acquired right now if the pool is idle: live keys × <see cref="PerKey"/>.</summary>
    public int MaxInFlight
    {
        get
        {
            lock (_gate)
                return LlmRuntimeConfig.ScaleInFlight(_perKey, LiveCountLocked());
        }
    }

    public async ValueTask<LlmKeyLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource<int>>? node;
        lock (_gate)
        {
            int ready = TryReserveLocked();
            if (ready >= 0)
                return CreateLease(ready, holdsSlot: true);
            if (LiveCountLocked() == 0)
                throw new InvalidOperationException(NoLiveKeyMessage);
            var waiter = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() => CancelWaiter(node));
        int slot = await node.Value.Task.ConfigureAwait(false);
        return CreateLease(slot, holdsSlot: true);
    }

    /// <summary>
    /// Picks a live key without taking a NER slot. Vision OCR uses this so its own
    /// <c>ocrConcurrency</c> cap is unchanged, while still sharing 401 disablement with NER.
    /// </summary>
    public bool TryBorrow(out LlmKeyLease lease)
    {
        lock (_gate)
        {
            int slot = PickLocked(requireFreeSlot: false);
            if (slot < 0)
            {
                lease = null!;
                return false;
            }

            lease = CreateLease(slot, holdsSlot: false);
            return true;
        }
    }

    /// <summary>Marks the key unusable for this process. In-flight calls still release. Returns false if it was already disabled.</summary>
    public bool Disable(int slot)
    {
        if ((uint)slot >= (uint)_keys.Length)
            return false;
        lock (_gate)
        {
            if (_disabled[slot])
                return false;
            _disabled[slot] = true;
            if (LiveCountLocked() == 0)
                FailWaitersLocked();
            return true;
        }
    }

    /// <summary>Replaces any pooled key found in <paramref name="text"/> so HTTP bodies can be logged.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || _redactOrder.Length == 0)
            return text ?? "";
        string result = text;
        foreach (string key in _redactOrder)
        {
            if (key.Length == 0 || !result.Contains(key, StringComparison.Ordinal))
                continue;
            result = result.Replace(key, "***", StringComparison.Ordinal);
        }

        return result;
    }

    internal void Release(int slot)
    {
        lock (_gate)
        {
            if ((uint)slot >= (uint)_inUse.Length)
                return;
            if (_inUse[slot] > 0)
                _inUse[slot]--;
            WakeOneLocked();
        }
    }

    private LlmKeyLease CreateLease(int slot, bool holdsSlot) =>
        new(this, slot, _keys[slot], holdsSlot);

    private int TryReserveLocked()
    {
        int slot = PickLocked(requireFreeSlot: true);
        if (slot < 0)
            return -1;
        _inUse[slot]++;
        return slot;
    }

    private int PickLocked(bool requireFreeSlot)
    {
        int count = _keys.Length;
        if (count == 0)
            return -1;
        int best = -1;
        int bestLoad = int.MaxValue;
        for (int n = 0; n < count; n++)
        {
            int index = (_cursor + n) % count;
            if (_disabled[index])
                continue;
            if (requireFreeSlot && _inUse[index] >= _perKey)
                continue;
            if (_inUse[index] < bestLoad)
            {
                best = index;
                bestLoad = _inUse[index];
            }
        }

        if (best >= 0)
            _cursor = (best + 1) % count;
        return best;
    }

    private int LiveCountLocked()
    {
        int live = 0;
        for (int i = 0; i < _disabled.Length; i++)
        {
            if (!_disabled[i])
                live++;
        }

        return live;
    }

    private void WakeOneLocked()
    {
        while (_waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            TaskCompletionSource<int> waiter = node.Value;
            if (waiter.Task.IsCompleted)
                continue;
            int slot = TryReserveLocked();
            if (slot < 0)
            {
                _waiters.AddFirst(node);
                return;
            }

            if (!waiter.TrySetResult(slot))
            {
                if (_inUse[slot] > 0)
                    _inUse[slot]--;
                continue;
            }

            return;
        }
    }

    private void FailWaitersLocked()
    {
        while (_waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            node.Value.TrySetException(new InvalidOperationException(NoLiveKeyMessage));
        }
    }

    private void CancelWaiter(LinkedListNode<TaskCompletionSource<int>> node)
    {
        lock (_gate)
        {
            if (node.List == _waiters)
                _waiters.Remove(node);
            node.Value.TrySetCanceled();
        }
    }

    private static void AddKey(List<string> keys, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        string key = raw.Trim();
        if (key.Length == 0)
            return;
        if (seen.Add(key))
            keys.Add(key);
    }

    private const string NoLiveKeyMessage = "No live LLM API key is available.";

    /// <summary>One borrowed key. Dispose releases a NER slot when this lease holds one. <see cref="ToString"/> omits the secret.</summary>
    public sealed class LlmKeyLease : IDisposable
    {
        private LlmKeyPool? _pool;
        private readonly bool _holdsSlot;

        internal LlmKeyLease(LlmKeyPool pool, int slot, string key, bool holdsSlot)
        {
            _pool = pool;
            _holdsSlot = holdsSlot;
            Slot = slot;
            Key = key;
        }

        /// <summary>Index into the resolved key list. Safe to log.</summary>
        public int Slot { get; }

        /// <summary>Bearer token. Do not log, interpolate into errors, or include in traces.</summary>
        public string Key { get; }

        public void Apply(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(Key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }

        public void Dispose()
        {
            LlmKeyPool? pool = Interlocked.Exchange(ref _pool, null);
            if (pool is not null && _holdsSlot)
                pool.Release(Slot);
        }

        public override string ToString() => "LlmKeyLease(slot=" + Slot.ToString() + ")";
    }
}

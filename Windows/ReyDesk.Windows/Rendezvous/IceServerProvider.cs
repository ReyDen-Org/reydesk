using ReyDesk.Protocol.Rendezvous.V1;

namespace ReyDesk.Windows.Rendezvous;

internal sealed record IceServerInfo(IReadOnlyList<string> Urls, string? Username, string? Credential);

internal sealed record IceConfigSnapshot(IReadOnlyList<IceServerInfo> Servers, DateTimeOffset ExpiresAt)
{
    public bool IsExpired(TimeSpan margin) => DateTimeOffset.UtcNow + margin >= ExpiresAt;
}

/// <summary>Holds the latest IceConfig from the rendezvous server: the STUN-only one after auth, and the one answering an
/// IceConfigRequest, which is sent only while the user allows relay (T18 H2). While relay is off TURN servers are dropped
/// on arrival, so no relay credential is held. Credentials are never logged or persisted.</summary>
internal sealed class IceServerProvider
{
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(10);
    private readonly Func<Task> _requestRefresh;
    private readonly Func<bool> _relayAllowed;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private readonly object _gate = new();
    private TaskCompletionSource<IceConfigSnapshot> _next = NewSource();

    public IceServerProvider(Func<Task> requestRefresh, Func<bool>? relayAllowed = null)
    {
        _requestRefresh = requestRefresh;
        _relayAllowed = relayAllowed ?? (() => false);
    }

    internal static bool IsTurn(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);

    private static bool HasTurn(IceConfigSnapshot s) =>
        s.Servers.Any(x => x.Credential is not null && x.Urls.Any(IsTurn));

    public IceConfigSnapshot? Current { get; private set; }

    public event Action<IceConfigSnapshot>? Changed;

    private static TaskCompletionSource<IceConfigSnapshot> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Update(IceConfig config)
    {
        var relay = _relayAllowed();
        var servers = config.IceServers
            .Select(s => new IceServerInfo(relay ? s.Urls.ToArray() : s.Urls.Where(u => !IsTurn(u)).ToArray(),
                relay && !string.IsNullOrEmpty(s.Username) ? s.Username : null,
                relay && !string.IsNullOrEmpty(s.Credential) ? s.Credential : null))
            .Where(s => s.Urls.Count > 0)
            .ToArray();
        var snapshot = new IceConfigSnapshot(servers, DateTimeOffset.FromUnixTimeMilliseconds(config.ExpiresAtMs));
        TaskCompletionSource<IceConfigSnapshot> waiter;
        lock (_gate)
        {
            Current = snapshot;
            waiter = _next;
            _next = NewSource();
        }
        waiter.TrySetResult(snapshot);
        Changed?.Invoke(snapshot);
    }

    /// <summary>Relay off: the STUN-only config from the initial IceConfig, never a request. Relay on: asks for TURN
    /// credentials when none are valid (at most once per 10 s) and falls back to what is held (STUN) when the server does
    /// not answer or has relay disabled.</summary>
    public async Task<IceConfigSnapshot> GetAsync(CancellationToken ct = default)
    {
        var current = Current;
        var relay = _relayAllowed();
        var request = relay && (current is null || !HasTurn(current) || current.IsExpired(TimeSpan.FromMinutes(1)));
        if (current is not null && (!request || DateTimeOffset.UtcNow - _lastRequest < MinRequestGap)) return current;

        Task<IceConfigSnapshot> wait;
        lock (_gate) wait = _next.Task;
        if (request)
        {
            _lastRequest = DateTimeOffset.UtcNow;
            try { await _requestRefresh().ConfigureAwait(false); }
            catch (Exception) when (current is not null) { return current; }
        }
        try { return await wait.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (TimeoutException) when (Current is { } held) { return held; }
    }
}

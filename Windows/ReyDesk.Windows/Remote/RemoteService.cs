using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Rendezvous;
using ReyDesk.Windows.Settings;

namespace ReyDesk.Windows.Remote;

/// <summary>What the UI draws for internet access. Immutable; read <see cref="RemoteService.State"/> after <see cref="RemoteService.Changed"/>.
/// Same fields as D3's RemoteUiState (MainPage.Connection.cs): T11 copies them with RemoteUiState.Current.Update.</summary>
internal sealed record RemoteState(
    string? DeskId, string? DeskIdDisplay, string? KeyId, RemoteServerState Server, string? ServerDetail, RDPathKind? Path,
    bool RemoteAllowed, bool RelayAllowed, string RendezvousUrl, string? RemoteCode, string? RemoteCodeDisplay,
    DateTimeOffset? CodeExpiresAt, PendingApproval? PendingApproval);

/// <summary>The Windows RemoteController (mirror of Android remote/RemoteController.kt): owns the rendezvous client lifecycle,
/// serves incoming relay pipes through the pairing listener, generates the typed pairing code (M4) and exposes the UI state.
/// All events are raised on background threads; marshal to the UI thread.</summary>
internal sealed class RemoteService : IAsyncDisposable
{
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);

    private readonly DeviceIdentityService _identity;
    private readonly AppSettingsService _settings;
    private readonly RendezvousIdentityAdapter _rendezvousIdentity;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private PairingListener? _listener;
    private RendezvousClient? _client;
    private Timer? _codeTimer;
    private RemoteServerState _server;
    private string? _serverDetail;
    private RDPathKind? _path;
    private string? _code;
    private DateTimeOffset? _codeExpires;
    private bool _started;
    private bool _disposed;

    public RemoteService(DeviceIdentityService identity, AppSettingsService settings)
    {
        _identity = identity;
        _settings = settings;
        _rendezvousIdentity = new RendezvousIdentityAdapter(identity);
        Gate = new RemotePairingApprovalGate();
        Gate.PendingChanged += RaiseChanged;
        _server = IsConfigured ? RemoteServerState.Offline : RemoteServerState.NotConfigured;
    }

    /// <summary>The IPairingApprovalGate given to PairingListener. T11: set <c>Gate.UiAttached = true</c> while the approval dialog
    /// host is alive, show <c>State.PendingApproval</c> when set, and call <c>PendingApproval.Resolve(approved, allowRemote)</c>.</summary>
    public RemotePairingApprovalGate Gate { get; }

    /// <summary>Canonical Desk ID for PairingListener's deskIdProvider and PairingOfferFactory.</summary>
    public string? DeskId => _identity.DeskId;
    /// <summary>Rendezvous URL for PairingOfferFactory (M3), or null when remote is off or not configured.</summary>
    public string? OfferRendezvousUrl => _settings.RemoteAllowed && IsConfigured ? _settings.RendezvousUrl : null;
    public bool IsConfigured => _settings.RendezvousUrl.Length > 0;

    public event Action? Changed;

    public RemoteState State
    {
        get
        {
            lock (_sync)
            {
                var deskId = _identity.DeskId;
                return new RemoteState(deskId, deskId is null ? null : DeskId_Display(deskId), KeyId.Display(KeyId.FromSpki(_identity.PublicKey)),
                    _server, _serverDetail, _path, _settings.RemoteAllowed, _settings.RelayAllowed, _settings.RendezvousUrl,
                    _code, _code is null ? null : Sas.Display(_code), _codeExpires, Gate.Pending);
            }
        }
    }

    private static string DeskId_Display(string canonical) => Identity.DeskId.Display(canonical);

    // ---------------- lifecycle ----------------

    /// <summary>Wires the pairing listener and starts the rendezvous client when remote is allowed and configured.</summary>
    public void Start(PairingListener listener)
    {
        lock (_sync)
        {
            if (_started || _disposed) return;
            _started = true;
            _listener = listener;
        }
        Apply();
    }

    /// <summary>Starts or stops the client to match the current settings (call after changing them; the Set* methods do).</summary>
    private void Apply()
    {
        RendezvousClient? stop = null;
        RendezvousClient? start = null;
        lock (_sync)
        {
            if (!_started || _disposed) return;
            var want = _settings.RemoteAllowed && IsConfigured;
            if (!want || (_client is not null && _clientUrl != _settings.RendezvousUrl)) { stop = _client; _client = null; }
            if (want && _client is null)
            {
                start = new RendezvousClient(_settings.RendezvousUrl, _rendezvousIdentity, appVersion: ReyDesk.Windows.Platform.AppInfo.Version,
                    relayAllowed: () => _settings.RelayAllowed);
                _client = start;
                _clientUrl = _settings.RendezvousUrl;
                start.StateChanged += state => OnClientState(start, state);
                start.PipeRequested += request => _ = HandlePipeRequestAsync(start, request);
                start.PairSlotStatusChanged += OnSlotStatus;
            }
            if (!want) { _server = IsConfigured ? RemoteServerState.Offline : RemoteServerState.NotConfigured; _serverDetail = null; _path = null; }
            else _server = _identity.DeskId is null ? RemoteServerState.Registering : RemoteServerState.Connecting;
        }
        if (stop is not null) { ClearCode(); _ = StopClientAsync(stop); }
        start?.Start();
        RaiseChanged();
    }

    private string _clientUrl = "";

    private static async Task StopClientAsync(RendezvousClient client)
    {
        try { await client.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[Remote] rendezvous stop failed: {ex.GetType().Name}"); }
    }

    private void OnClientState(RendezvousClient client, RendezvousState state)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(client, _client)) return;
            switch (state)
            {
                case RendezvousState.Connecting: _server = RemoteServerState.Connecting; _serverDetail = null; break;
                case RendezvousState.Authenticating:
                    _server = _identity.DeskId is null ? RemoteServerState.Registering : RemoteServerState.Connecting; break;
                case RendezvousState.Online:
                    _server = _identity.DeskId is null ? RemoteServerState.NoId : RemoteServerState.Online; _serverDetail = null; break;
                case RendezvousState.Backoff:
                    _server = RemoteServerState.Offline; _serverDetail = client.LastError; break;
                default: _server = RemoteServerState.Offline; break;
            }
        }
        if (state != RendezvousState.Online) ClearCode();
        RaiseChanged();
    }

    /// <summary>Retry now (the "Retry" button): restarts the client, which re-registers if the Desk ID is missing.</summary>
    public void Retry()
    {
        RendezvousClient? old;
        lock (_sync) { old = _client; _client = null; }
        if (old is not null) { ClearCode(); _ = StopClientAsync(old); }
        Apply();
    }

    public void SetRemoteAllowed(bool allowed)
    {
        _settings.SetRemoteAllowed(allowed);
        Apply();
    }

    public void SetRelayAllowed(bool allowed)
    {
        _settings.SetRelayAllowed(allowed);
        RaiseChanged();
    }

    /// <summary>Throws ArgumentException for anything but wss:// (loopback ws:// in Debug). Empty clears.</summary>
    public void SetRendezvousUrl(string url)
    {
        _settings.SetRendezvousUrl(url);
        Apply();
    }

    // ---------------- ICE ----------------

    /// <summary>ICE servers for a remote session: STUN always, TURN only when the owner allowed relay. LAN sessions pass none.</summary>
    public async Task<IReadOnlyList<IceServerInfo>> GetRemoteIceServersAsync(CancellationToken ct)
    {
        RendezvousClient? client;
        lock (_sync) client = _client;
        if (client is null) return Array.Empty<IceServerInfo>();
        IceConfigSnapshot config;
        try { config = await client.IceServers.GetAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Remote] ICE config unavailable: {ex.GetType().Name}");
            return Array.Empty<IceServerInfo>();
        }
        return FilterIceServers(config.Servers, _settings.RelayAllowed);
    }

    internal static IReadOnlyList<IceServerInfo> FilterIceServers(IReadOnlyList<IceServerInfo> servers, bool relayAllowed)
    {
        var result = new List<IceServerInfo>();
        foreach (var server in servers)
        {
            var urls = server.Urls.Where(u => relayAllowed || !IsTurn(u)).ToArray();
            if (urls.Length > 0) result.Add(server with { Urls = urls });
        }
        return result;
    }

    private static bool IsTurn(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);

    // ---------------- path ----------------

    /// <summary>Called by SessionCoordinator with the selected path of the current session (null when it ends).</summary>
    public void ReportPath(RDPathKind? path)
    {
        lock (_sync) { if (_path == path) return; _path = path; }
        RaiseChanged();
    }

    public static RDPathKind? ToUiPath(PathKind path) => path switch
    {
        PathKind.PathLanDirect => RDPathKind.Lan,
        PathKind.PathRemoteDirect => RDPathKind.Direct,
        PathKind.PathRelayed => RDPathKind.Relay,
        _ => null,
    };

    /// <summary>Support text for the diagnostics view. Never contains tokens, credentials, codes or the full URL path.</summary>
    public string DiagnosticsText()
    {
        RendezvousClient? client;
        lock (_sync) client = _client;
        var s = State;
        var ice = client?.IceServers.Current;
        var stun = ice?.Servers.SelectMany(x => x.Urls).Count(u => !IsTurn(u)) ?? 0;
        var turn = ice?.Servers.SelectMany(x => x.Urls).Count(IsTurn) ?? 0;
        var host = Uri.TryCreate(s.RendezvousUrl, UriKind.Absolute, out var uri) ? uri.Host : "-";
        return $"Server: {s.Server} ({host}); detail: {s.ServerDetail ?? "-"}\n" +
               $"Desk ID: {s.DeskIdDisplay ?? "-"}; Key ID: {s.KeyId}\n" +
               $"Remote allowed: {s.RemoteAllowed}; relay allowed: {s.RelayAllowed}; path: {s.Path?.ToString() ?? "-"}\n" +
               $"ICE servers: {stun} STUN, {turn} TURN (TURN used only when relay is allowed)";
    }

    // ---------------- relay pipes ----------------

    private async Task HandlePipeRequestAsync(RendezvousClient client, IncomingPipeRequest request)
    {
        var ct = _lifetime.Token;
        try
        {
            var listener = _listener;
            Console.WriteLine($"[Remote] {DateTimeOffset.UtcNow:HH:mm:ss}Z incoming pipe offered: {request.Purpose}");
            if (listener is null || !_settings.RemoteAllowed || !listener.ShouldAcceptPipe(request.Purpose, request.RequesterFingerprint))
            {
                var why = listener is null ? "pairing listener not ready" : !_settings.RemoteAllowed ? "internet access is off"
                    : request.Purpose == PipePurpose.PairCode ? "no active internet pairing code (or comparison paused)"
                    : request.Purpose == PipePurpose.PairQr ? "no current QR offer on the Connection panel"
                    : "phone not allowed over the internet";
                Console.WriteLine($"[Remote] incoming pipe declined: {why}");
                await request.DeclineAsync(ct).ConfigureAwait(false);
                return;
            }
            var pipe = await request.AcceptAsync(ct).ConfigureAwait(false);
            if (pipe is null) { Console.WriteLine("[Remote] incoming pipe accepted but the server never opened it"); return; }
            await listener.HandlePipeAsync(pipe, new PairingContext(ConnectionMedium.Remote, request.Purpose,
                request.RequesterFingerprint, request.RequesterDeskId), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Remote] incoming pipe failed: {ex.GetType().Name}");
        }
    }

    // ---------------- M4 remote code ----------------

    /// <summary>"Add phone over internet": creates a fresh 6 digit code, arms the host half and the server slot, expires after 5
    /// minutes. Replaces any previous code. Returns false (nothing armed) unless the server is online and the Desk ID is known.</summary>
    public async Task<bool> GenerateRemoteCodeAsync()
    {
        RendezvousClient? client;
        PairingListener? listener;
        lock (_sync) { client = _client; listener = _listener; }
        if (client is null || listener is null || client.State != RendezvousState.Online || _identity.DeskId is null) return false;
        var presentation = PairingOfferFactory.CreateRemoteCode();
        try
        {
            listener.ArmRemoteCode(presentation.Code, presentation.ExpiresAt);
            await client.CreatePairSlotAsync(presentation.Code, (int)CodeTtl.TotalSeconds, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            listener.DisarmRemoteCode();
            Console.WriteLine($"[Remote] code slot failed: {ex.GetType().Name}");
            return false;
        }
        lock (_sync)
        {
            _code = presentation.Code;
            _codeExpires = presentation.ExpiresAt;
            _codeTimer?.Dispose();
            _codeTimer = new Timer(_ => CancelRemoteCode(), null, presentation.ExpiresAt - DateTimeOffset.UtcNow, Timeout.InfiniteTimeSpan);
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Cancel button, expiry, or success: disarms the host half and cancels the server slot.</summary>
    public void CancelRemoteCode()
    {
        RendezvousClient? client;
        lock (_sync) client = _client;
        if (ClearCode() && client is not null)
            _ = client.CancelPairSlotAsync().ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Returns true when a code was active.</summary>
    private bool ClearCode()
    {
        bool had;
        lock (_sync)
        {
            had = _code is not null;
            _code = null;
            _codeExpires = null;
            _codeTimer?.Dispose();
            _codeTimer = null;
        }
        _listener?.DisarmRemoteCode();
        if (had) RaiseChanged();
        return had;
    }

    private void OnSlotStatus(PairSlotStatusInfo status)
    {
        if (status.Expired || status.AttemptsLeft <= 0) ClearCode();
    }

    /// <summary>SessionCoordinator calls this for every paired session: a code pairing consumes the single-use code.</summary>
    public void OnPaired(PairedSession session)
    {
        if (session.Method == PairingMethod.RemoteCode && ClearCode() && _client is { } client)
            _ = client.CancelPairSlotAsync().ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[Remote] state listener failed: {ex.GetType().Name}"); }
    }

    public async ValueTask DisposeAsync()
    {
        RendezvousClient? client;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            client = _client;
            _client = null;
            _codeTimer?.Dispose();
        }
        _lifetime.Cancel();
        _listener?.DisarmRemoteCode();
        if (client is not null) await StopClientAsync(client).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

using System;
using System.Collections.Generic;
using Google.Protobuf;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Media;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Signaling;
using SIPSorcery.Net;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Companion;
using ReyDesk.Windows.Remote;
using ReyDesk.Windows.Rendezvous;
using System.Threading.Tasks;
using ReyDesk.Windows.Transfer;

namespace ReyDesk.Windows;

/// <summary>
/// Owns the app's long-lived, stateful services (pairing, WebRTC signaling/capture) -
/// a simple composition root rather than a DI container, consistent with
/// App.xaml.cs's existing "shared services as a static holder" note. Exposed the same
/// way (<c>App.Session</c>), constructed once in App.OnLaunched, disposed alongside
/// TrayIcon.
///
/// A device pairs and gets a session automatically - there's no separate "start
/// sharing" step in this UI (see MainPage's Devices panel, which reflects
/// <see cref="SessionStateChanged"/> as a live per-device status).
/// </summary>
internal sealed class SessionCoordinator : IDisposable
{
    private readonly DeviceIdentityService _identity;
    private readonly PairingListener _pairingListener;
    private readonly Dictionary<string, RTCPeerConnectionState> _sessionStates = new();
    private readonly Dictionary<string, ConnectionMetrics> _streamStats = new();
    private readonly Dictionary<string, NegotiatedConfig> _streamConfigs = new();
    private readonly object _sessionsLock = new();
    private readonly SessionRegistry<SessionTeardown> _sessions = new();
    private readonly Dictionary<string, CompanionSession> _companions = new();
    private sealed record CompanionSession(WebRtcSession Media, SessionTeardown Owner, CompanionPolicy Policy, string Id, byte[] Key)
    { public FileTransferEngine? Transfer { get; set; } public bool Unattended { get; init; } public bool Remote { get; init; } }
    private bool _disposed;
    private PairingOffer? _currentOffer;
    private PairingDiscovery? _discovery;

    public TrustedDeviceStore TrustedDevices { get; }
    /// <summary>Internet access: rendezvous client, remote code, UI state and the approval gate (T11 binds to this).</summary>
    public RemoteService Remote { get; }
    /// <summary>Starts the rendezvous client when internet access is on and configured. Called once from App.OnLaunched.</summary>
    public void StartRemote() => Remote.Start(_pairingListener);
    public string LocalEndpoint => _currentOffer is null ? "Unavailable" : $"{_currentOffer.LocalIpHint}:{_currentOffer.LocalPortHint}";

    /// <summary>Raised whenever a paired device's WebRTC session connection state
    /// changes - MainPage uses this to refresh the Devices panel's live status.</summary>
    public event Action<string /* deviceId */>? SessionStateChanged;
    public event Action<string>? StreamStatsChanged;
    public event Action<string, string, PhoneHubEvent>? PhoneHubEventReceived;
    public event Action<string, string, CompanionResult>? CompanionResultReceived;
    public event Func<DeviceIdentity, Task<bool>>? DeviceApprovalRequested;
    public event Func<string, string, ApprovedAction, Task<bool>>? ApprovedActionRequested;
    public event Func<string, string, DeveloperTaskDefinition, bool, Task<bool>>? DeveloperTaskRequested;
    public event Func<string, bool, string, Func<Action, bool>, Task<CompanionResult>>? ClipboardRequested;

    public event Func<string, string, FileTransferOffer, Task<TransferDestination?>>? FileReceiveRequested;
    public event Action<string, string, TransferStatus>? FileProgressChanged;
    public string? TryGetSessionId(string deviceId)
    {
        lock (_sessionsLock) return _companions.TryGetValue(deviceId, out var c) && IsAuthorized(deviceId, c) ? c.Id : null;
    }
    public Task<bool> SendFileAsync(string deviceId, Stream source, string name, long length, string? expectedSessionId = null)
    {
        FileTransferEngine? transfer = null;
        lock (_sessionsLock)
        {
            if (_companions.TryGetValue(deviceId, out var c) && IsAuthorized(deviceId, c) &&
                (expectedSessionId is null || expectedSessionId == c.Id)) transfer = c.Transfer;
        }
        if (transfer is not null) return transfer.SendAsync(source, name, length);
        source.Dispose();
        return Task.FromResult(false);
    }
    public void CancelFile(string deviceId)
    {
        lock (_sessionsLock) if (_companions.TryGetValue(deviceId, out var c)) c.Transfer?.Cancel();
    }

    private Task<bool> ApproveNewDevice(DeviceIdentity device) =>
        DeviceApprovalRequested?.Invoke(device) ?? Task.FromResult(false);

    public bool SendCompanionRequest(string deviceId, CompanionRequest request)
    {
        lock (_sessionsLock)
        {
            if (!_companions.TryGetValue(deviceId, out var context) || !IsAuthorized(deviceId, context) ||
                !context.Policy.Accept(request, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return false;
            return context.Media.TrySendControl(new Envelope { CompanionRequest = request });
        }
    }
    private bool IsAuthorized(string id, CompanionSession context) =>
        !_disposed && _sessions.IsCurrent(id, context.Owner) && TrustedDevices.IsTrusted(id, context.Key) &&
        (!context.Unattended || TrustedDevices.IsUnattendedAllowed(id, context.Key)) &&
        SessionAccessPolicy.RemoteSessionPermitted(context.Remote, App.AppSettings.RemoteAllowed, TrustedDevices.IsRemoteAllowed(id, context.Key));
    public bool IsCurrentSession(string id, string sessionId)
    {
        lock (_sessionsLock) return _companions.TryGetValue(id, out var context) && context.Id == sessionId && IsAuthorized(id, context);
    }

    public ConnectionMetrics? TryGetStreamStats(string deviceId)
    {
        lock (_sessionsLock) return _streamStats.TryGetValue(deviceId, out var stats) ? stats : null;
    }

    public NegotiatedConfig? TryGetNegotiatedConfig(string deviceId)
    {
        lock (_sessionsLock) return _streamConfigs.TryGetValue(deviceId, out var config) ? config.Clone() : null;
    }

    public SessionCoordinator(DeviceIdentityService identity)
    {
        _identity = identity;
        TrustedDevices = TrustedDeviceStore.LoadOrCreate();
        Remote = new RemoteService(identity, App.AppSettings);
        // Until T11 attaches its approval dialog (Remote.Gate.UiAttached), prompts go through the legacy device dialog.
        Remote.Gate.Fallback = async (request, _) => new PairingApprovalResult(await ApproveNewDevice(request.Peer));
        _pairingListener = new PairingListener(TrustedDevices, () => _currentOffer, identity, Remote.Gate, () => Remote.DeskId);
        _pairingListener.Paired += OnPaired;
        _pairingListener.Start();
        TrustedDevices.Changed += OnTrustChanged;
        // M1: turning "Allow over internet" off (RemoteService.Changed) must end live remote sessions too.
        Remote.Changed += OnTrustChanged;
    }

    private void OnTrustChanged()
    {
        var revoked = new List<SessionTeardown>();
        lock (_sessionsLock)
            foreach (var pair in _companions)
                if (!IsAuthorized(pair.Key, pair.Value)) revoked.Add(pair.Value.Owner);
        foreach (var owner in revoked) owner.Dispose();
    }

    /// <summary>Generates a fresh pairing offer and remembers it as the one
    /// <see cref="PairingListener"/> validates incoming requests against - replacing
    /// whichever offer was active before (e.g. on "Regenerate" or countdown expiry).</summary>
    public PairingPresentation CreatePairingOffer()
    {
        var presentation = PairingOfferFactory.Create(_identity, Remote.DeskId, Remote.OfferRendezvousUrl);
        _currentOffer = presentation.Offer;
        try
        {
            _discovery ??= new PairingDiscovery();
            _discovery.Publish(presentation.Offer);
        }
        catch (Exception ex)
        {
            // QR remains usable on networks that disable multicast discovery.
            Console.WriteLine($"[PairingDiscovery] LAN discovery unavailable: {ex.Message}");
        }
        return presentation;
    }

    /// <summary>Live connection state for a paired device's active session, if any -
    /// null means never connected this run (still just "Paired").</summary>
    public RTCPeerConnectionState? TryGetSessionState(string deviceId)
    {
        lock (_sessionsLock) return _sessionStates.TryGetValue(deviceId, out var state) ? state : null;
    }

    /// <summary>Ends a device's active session (if any) by closing its signaling
    /// connection - the same teardown that happens if the remote side disconnects,
    /// see the <c>connection.Closed</c> handler below. No-op if the device has no
    /// active connection.</summary>
    public void DisconnectDevice(string deviceId)
    {
        SessionTeardown? current;
        lock (_sessionsLock) current = _sessions.Get(deviceId);
        current?.Dispose();
    }

    private void OnPaired(PairedSession paired)
    {
        Remote.OnPaired(paired);
        if (paired.Medium != ConnectionMedium.Remote)
        {
            StartPairedSession(paired, Array.Empty<IceServerInfo>());
            return;
        }
        _ = StartRemotePairedAsync(paired);
    }

    private async Task StartRemotePairedAsync(PairedSession paired)
    {
        IReadOnlyList<IceServerInfo> iceServers = Array.Empty<IceServerInfo>();
        try { iceServers = await Remote.GetRemoteIceServersAsync(default).ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[SessionCoordinator] ICE config failed: {ex.GetType().Name}"); }
        if (_disposed) { paired.Owner.Dispose(); return; }
        try { StartPairedSession(paired, iceServers); }
        catch (Exception ex)
        {
            Console.WriteLine($"[SessionCoordinator] remote session setup failed: {ex.GetType().Name}");
            paired.Owner.Dispose();
        }
    }

    private void StartPairedSession(PairedSession paired, IReadOnlyList<IceServerInfo> iceServers)
    {
        var device = paired.Peer;
        var remote = paired.Medium == ConnectionMedium.Remote;
        var connection = new SignalingConnection(paired.Stream, paired.Owner, paired.WasUnattended, paired.PeerPublicKey);
        var authenticatedKey = connection.AuthenticatedPeerPublicKey;
        if (authenticatedKey.Length == 0 || !TrustedDevices.IsTrusted(device.DeviceId, authenticatedKey)) { connection.Dispose(); return; }
        var sessionId = Guid.NewGuid().ToString();
        var session = new WebRtcSession(connection, sessionId, App.AppSettings.Streaming, App.AppSettings.StreamPreset, iceServers, remote);
        FileTransferEngine? transfer = null;
        var owner = new SessionTeardown(() => { transfer?.Dispose(); session.Dispose(); }, connection.Dispose);
        var context = new CompanionSession(session, owner, new CompanionPolicy(), sessionId,
            authenticatedKey) { Unattended = connection.WasUnattendedReconnect, Remote = remote };

        transfer = new FileTransferEngine(envelope =>
        {
            lock (_sessionsLock) return IsAuthorized(device.DeviceId, context) && session.TrySendControl(envelope);
        }, () =>
        {
            lock (_sessionsLock) return IsAuthorized(device.DeviceId, context);
        }, offer => FileReceiveRequested?.Invoke(device.DeviceId, sessionId, offer) ?? Task.FromResult<TransferDestination?>(null));
        context.Transfer = transfer;
        transfer.Progress += status => FileProgressChanged?.Invoke(device.DeviceId, sessionId, status);

        session.NegotiatedConfigChanged += config =>
        {
            lock (_sessionsLock)
            {
                if (!_sessions.IsCurrent(device.DeviceId, owner)) return;
                _streamConfigs[device.DeviceId] = config.Clone();
            }
            StreamStatsChanged?.Invoke(device.DeviceId);
        };

        session.ConnectionStateChanged += state =>
        {
            Console.WriteLine($"[SessionCoordinator] {device.DisplayName}: {state}");
            lock (_sessionsLock)
            {
                if (!_sessions.IsCurrent(device.DeviceId, owner)) return;
                _sessionStates[device.DeviceId] = state;
            }
            SessionStateChanged?.Invoke(device.DeviceId);
            if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                owner.Dispose();
        };
        void SendStatus()
        {
            var status = new Envelope
            {
                ProtocolVersion = 1,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ConnectionStatus = new ConnectionStatus
                {
                    State = ConnectionState.Connected,
                    Medium = remote ? ConnectionMedium.Remote : ConnectionMedium.Local,
                    DiagnosticDetail = "Connected from Windows.",
                    Path = session.Path,
                },
            };
            session.TrySendControl(status);
        }
        session.ControlChannelOpen += SendStatus;
        session.PathChanged += path =>
        {
            lock (_sessionsLock) if (!_sessions.IsCurrent(device.DeviceId, owner)) return;
            if (remote) Remote.ReportPath(RemoteService.ToUiPath(path));
            SendStatus();
        };
        session.ControlMessageReceived += data =>
        {
            try
            {
                if (data.Length > 65536) return;
                var envelope = Envelope.Parser.ParseFrom(data);
                if (envelope.ProtocolVersion != 1 || envelope.SessionId != sessionId) return;
                lock (_sessionsLock) if (!IsAuthorized(device.DeviceId, context)) return;
                if (envelope.PayloadCase == Envelope.PayloadOneofCase.PhoneHubEvent)
                {
                    if (CompanionPolicy.ValidEvent(envelope.PhoneHubEvent))
                        PhoneHubEventReceived?.Invoke(device.DeviceId, sessionId, envelope.PhoneHubEvent);
                }
                else if (envelope.PayloadCase == Envelope.PayloadOneofCase.CompanionResult)
                {
                    if (envelope.CompanionResult.RequestId.Length <= 64 && envelope.CompanionResult.Detail.Length <= 8192)
                        CompanionResultReceived?.Invoke(device.DeviceId, sessionId, envelope.CompanionResult);
                }
                else if (envelope.PayloadCase == Envelope.PayloadOneofCase.CompanionRequest)
                {
                    _ = HandleCompanionAsync(device.DeviceId, context, envelope.CompanionRequest);
                }
                else if (envelope.PayloadCase is Envelope.PayloadOneofCase.FileTransferOffer or
                    Envelope.PayloadOneofCase.FileTransferResponse or Envelope.PayloadOneofCase.FileTransferProgress or
                    Envelope.PayloadOneofCase.FileTransferControl or Envelope.PayloadOneofCase.FileTransferChunk)
                {
                    _ = HandleTransferAsync(transfer, envelope);
                }
                else if (envelope.PayloadCase == Envelope.PayloadOneofCase.StreamStats)
                {
                    var stats = envelope.StreamStats;
                    session.ObserveConnectionQuality(stats);
                    static double? Finite(double value) => double.IsFinite(value) && value >= 0 ? value : null;
                    var metrics = new ConnectionMetrics(
                        stats.FpsAvailable ? Finite(stats.Fps) : null,
                        stats.BitrateAvailable ? stats.BitrateKbps : null,
                        stats.RoundTripTimeAvailable ? stats.RoundTripTimeMs : null,
                        stats.PacketLossAvailable && stats.PacketLossPercent <= 100 ? Finite(stats.PacketLossPercent) : null,
                        stats.JitterAvailable ? Finite(stats.JitterMs) : null,
                        (int)Math.Min(stats.WidthPx, 16384u), (int)Math.Min(stats.HeightPx, 16384u), stats.FramesDropped);
                    lock (_sessionsLock)
                    {
                        if (!_sessions.IsCurrent(device.DeviceId, owner)) return;
                        _streamStats[device.DeviceId] = metrics;
                    }
                    StreamStatsChanged?.Invoke(device.DeviceId);
                }
                else Console.WriteLine($"[SessionCoordinator] control message from {device.DisplayName}: {envelope.PayloadCase}");
            }
            catch (Exception)
            {
                Console.WriteLine($"[SessionCoordinator] malformed control message from {device.DisplayName}");
            }
        };
        connection.Closed += () =>
        {
            Console.WriteLine($"[SessionCoordinator] connection closed with {device.DisplayName}");
            owner.Dispose();
            bool removed;
            lock (_sessionsLock)
            {
                removed = _sessions.Remove(device.DeviceId, owner);
                if (removed)
                {
                    _sessionStates.Remove(device.DeviceId);
                    _streamStats.Remove(device.DeviceId);
                    _streamConfigs.Remove(device.DeviceId);
                    _companions.Remove(device.DeviceId);
                }
            }
            if (removed)
            {
                if (remote) Remote.ReportPath(null);
                SessionStateChanged?.Invoke(device.DeviceId);
            }
        };

        SessionTeardown? previous;
        lock (_sessionsLock)
        {
            if (_disposed || !TrustedDevices.IsTrusted(device.DeviceId, context.Key) ||
                (context.Unattended && !TrustedDevices.IsUnattendedAllowed(device.DeviceId, context.Key)) ||
                !SessionAccessPolicy.RemoteSessionPermitted(remote, App.AppSettings.RemoteAllowed, TrustedDevices.IsRemoteAllowed(device.DeviceId, context.Key))) previous = owner;
            else
            {
                previous = _sessions.Replace(device.DeviceId, owner);
                _sessionStates.Remove(device.DeviceId);
                _streamStats.Remove(device.DeviceId);
                _streamConfigs.Remove(device.DeviceId);
                _companions[device.DeviceId] = context;
            }
        }
        // Never close sessions under the coordinator lock: Close raises callbacks.
        previous?.Dispose();
        lock (_sessionsLock)
            if (!_sessions.IsCurrent(device.DeviceId, owner)) return;
        connection.StartReadLoop();
        _ = StartSessionAsync(session, device, owner);

    }

    private static async Task HandleTransferAsync(FileTransferEngine transfer, Envelope envelope)
    {
        try { await transfer.HandleAsync(envelope); }
        catch (Exception) { Console.WriteLine("[Transfer] Transfer could not be handled; canceled."); transfer.Cancel(); }
    }

    private async Task HandleCompanionAsync(string id, CompanionSession context, CompanionRequest request)
    {
        var result = new CompanionResult { RequestId = request.RequestId };
        try
        {
            lock (_sessionsLock)
            {
                if (!IsAuthorized(id, context)) return;
                if (!context.Policy.Accept(request, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                { result.Detail = "Unsupported, duplicate or rate-limited request."; context.Media.TrySendControl(new Envelope { CompanionResult = result }); return; }
            }
            if (request.Operation == "developer_status")
            {
                result.Success = true; result.Detail = DeveloperTaskService.Instance.ListPublic();
            }
            else if (request.Operation is "developer_start" or "developer_stop")
            {
                if (!InteractiveDesktop.IsUnlocked()) throw new UnauthorizedAccessException();
                result = await DeveloperTaskService.Instance.InvokeAsync(request.ActionId, request.Operation == "developer_stop",
                    (task, stop) => DeveloperTaskRequested?.Invoke(id, context.Id, task, stop) ?? Task.FromResult(false), action =>
                    {
                        lock (_sessionsLock)
                        {
                            if (!IsAuthorized(id, context) || !InteractiveDesktop.IsUnlocked()) return false;
                            action(); return true;
                        }
                    });
                result.RequestId = request.RequestId;
            }
            else if (request.Operation == "list_actions")
            {
                result.Success = true;
                result.Detail = ApprovedActionService.Instance.ListPublic();
            }
            else if (request.Operation == "approved_action")
            {
                if (!InteractiveDesktop.IsUnlocked()) throw new UnauthorizedAccessException();
                result = await ApprovedActionService.Instance.InvokeAsync(request.ActionId,
                    action => ApprovedActionRequested?.Invoke(id, context.Id, action) ?? Task.FromResult(false), action =>
                    {
                        lock (_sessionsLock)
                        {
                            if (!IsAuthorized(id, context) || !InteractiveDesktop.IsUnlocked()) return false;
                            action(); return true;
                        }
                    });
                result.RequestId = request.RequestId;
            }
            else if (request.Operation is "clipboard_get" or "clipboard_set")
            {
                var handler = ClipboardRequested;
                result = handler is null ? new CompanionResult { Detail = "Clipboard requires approval on the PC." }
                    : await handler(id, request.Operation == "clipboard_get", request.Text, action =>
                    {
                        lock (_sessionsLock)
                        {
                            if (!IsAuthorized(id, context) || !InteractiveDesktop.IsUnlocked()) return false;
                            action();
                            return true;
                        }
                    });
                result.RequestId = request.RequestId;
                if (result.Detail.Length > 8192) result = new CompanionResult { RequestId = request.RequestId, Detail = "Clipboard exceeds text limit." };
            }
            else
            {
                lock (_sessionsLock)
                {
                    if (!IsAuthorized(id, context)) return;
                    if (request.Operation is "desktop" or "control")
                    {
                        context.Media.SetDesktopEnabled(request.Operation == "desktop");
                        result.Success = true;
                        result.Detail = request.Operation == "control" ? "Control mode; desktop capture stopped." : "Desktop capture enabled.";
                    }
                    else
                    {
                        result.Success = SystemActions.Execute(request.Operation);
                        result.Detail = result.Success ? "System accepted the key/action; app response may vary." : "Windows did not accept the action.";
                    }
                }
            }
        }
        catch (Exception) { result.Success = false; result.Detail = "Action could not be completed."; }
        lock (_sessionsLock)
            if (IsAuthorized(id, context)) context.Media.TrySendControl(new Envelope { CompanionResult = result });
    }

    private static async System.Threading.Tasks.Task StartSessionAsync(WebRtcSession session, DeviceIdentity device, SessionTeardown owner)
    {
        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SessionCoordinator] WebRtcSession.StartAsync failed for {device.DisplayName}: {ex}");
            owner.Dispose();
        }
    }

    public void Dispose()
    {
        SessionTeardown[] sessions;
        lock (_sessionsLock)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Drain();
            _sessionStates.Clear();
            _streamStats.Clear();
            _streamConfigs.Clear();
            _companions.Clear();
        }
        _pairingListener.Paired -= OnPaired;
        TrustedDevices.Changed -= OnTrustChanged;
        Remote.Changed -= OnTrustChanged;
        _pairingListener.Dispose();
        try { Remote.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Console.WriteLine($"[Remote] shutdown failed: {ex.GetType().Name}"); }
        try { _discovery?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[PairingDiscovery] Discovery shutdown failed: {ex.Message}"); }
        foreach (var session in sessions) session.Dispose();
    }
}

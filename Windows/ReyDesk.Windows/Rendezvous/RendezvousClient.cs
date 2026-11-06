using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Google.Protobuf;
using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Windows.Identity;

namespace ReyDesk.Windows.Rendezvous;

/// <summary>Failed = auth refused in a way a retry cannot fix (BAD_SIGNATURE, KEY_MISMATCH): no reconnect loop; see LastError.</summary>
internal enum RendezvousState { Stopped, Connecting, Authenticating, Online, Backoff, Failed }

internal sealed class RendezvousPipeException : IOException
{
    public RendezvousPipeException(PipeFailReason reason) : base("Relay pipe failed: " + reason) => Reason = reason;
    public PipeFailReason Reason { get; }
}

internal sealed class RendezvousAuthException : IOException
{
    public RendezvousAuthException(AuthError error, TimeSpan? retryAfter = null) : base("Rendezvous auth refused: " + error)
    {
        Error = error;
        RetryAfter = retryAfter;
    }
    public AuthError Error { get; }
    /// <summary>Server-provided retry-after (from the Error frame that follows a RATE_LIMITED answer).</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The server sent an Error frame instead of the expected hello or auth answer (e.g. RATE_LIMITED).</summary>
internal sealed class RendezvousServerException : IOException
{
    public RendezvousServerException(ErrorCode code, TimeSpan? retryAfter) : base("Rendezvous service error: " + code)
    {
        Code = code;
        RetryAfter = retryAfter;
    }
    public ErrorCode Code { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Host side of an incoming pipe: Accept or Decline within 10 s (the service times it out).</summary>
internal sealed class IncomingPipeRequest
{
    private readonly RendezvousClient _client;
    private readonly TaskCompletionSource<RelayPipeStream> _opened;

    internal IncomingPipeRequest(RendezvousClient client, IncomingPipe frame, TaskCompletionSource<RelayPipeStream> opened)
    {
        _client = client;
        _opened = opened;
        PipeId = frame.PipeId;
        RequesterDeskId = frame.RequesterDeskId;
        RequesterFingerprint = frame.RequesterFp.ToByteArray();
        Purpose = frame.Purpose;
    }

    public string PipeId { get; }
    public string RequesterDeskId { get; }
    /// <summary>SHA-256(SPKI) of the requester key as authenticated by the service.</summary>
    public byte[] RequesterFingerprint { get; }
    public PipePurpose Purpose { get; }

    public Task<RelayPipeStream?> AcceptAsync(CancellationToken ct = default) => _client.DecideAsync(this, true, _opened, ct);
    public Task DeclineAsync(CancellationToken ct = default) => _client.DecideAsync(this, false, _opened, ct);
}

internal readonly record struct PairSlotStatusInfo(int AttemptsLeft, bool Expired);

/// <summary>Client of the ReyDesk rendezvous service (GET /v1/ws, protobuf binary frames).
/// Connects with backoff, authenticates with the device key, learns/persists the Desk ID, keeps the ICE config
/// fresh, manages the pairing slot and opens/accepts relay pipes. Never logs keys, codes, nonces or pipe bytes.</summary>
internal sealed class RendezvousClient : IAsyncDisposable
{
    private const int MaxFrame = 64 * 1024;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(70);
    private static readonly TimeSpan BackoffReset = TimeSpan.FromMinutes(5);

    private readonly Uri _url;
    private readonly string _signedUrl;
    private readonly IRendezvousIdentity _identity;
    private readonly string _platform;
    private readonly string _appVersion;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, RelayPipeStream> _pipes = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<RelayPipeStream>> _opens = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayPipeStream>> _incoming = new();
    private TaskCompletionSource _online = NewGate();
    private readonly object _stateGate = new();

    private CancellationTokenSource? _cts;
    private Task? _run;
    private ClientWebSocket? _ws;
    private long _seq;
    private volatile RendezvousState _state = RendezvousState.Stopped;
    private volatile string? _lastError;

    /// <param name="url">Service endpoint, e.g. wss://host/v1/ws. Normalised with RendezvousUrl; throws ArgumentException when not acceptable.</param>
    /// <param name="signedUrl">The service's configured PUBLIC_WS_URL, signed into the auth payload. Defaults to the normalised url.</param>
    /// <param name="relayAllowed">Read at every ICE decision: TURN credentials are requested and kept only while this is true. Default: never.</param>
    public RendezvousClient(string url, IRendezvousIdentity identity, string? signedUrl = null,
        string platform = "windows", string appVersion = "", Func<bool>? relayAllowed = null,
        bool allowCleartextForDev = RendezvousUrl.DevCleartextDefault)
    {
        var normalized = RendezvousUrl.Require(url, allowCleartextForDev, nameof(url));
        _url = new Uri(normalized);
        _signedUrl = signedUrl is null ? normalized : RendezvousUrl.Require(signedUrl, allowCleartextForDev, nameof(signedUrl));
        _identity = identity;
        _platform = platform;
        _appVersion = appVersion;
        IceServers = new IceServerProvider(RequestIceConfigAsync, relayAllowed);
    }

    public IceServerProvider IceServers { get; }
    public RendezvousState State => _state;
    public string? DeskId => _identity.DeskId;
    /// <summary>Exception type or auth error of the last failed attempt (no payloads).</summary>
    public string? LastError => _lastError;

    public event Action<RendezvousState>? StateChanged;
    public event Action<IncomingPipeRequest>? PipeRequested;
    public event Action<PairSlotStatusInfo>? PairSlotStatusChanged;

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------- lifecycle ----------------

    public void Start()
    {
        lock (_stateGate)
        {
            if (_run is not null) return;
            _cts = new CancellationTokenSource();
            _run = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_stateGate)
        {
            cts = _cts;
            run = _run;
            _cts = null;
            _run = null;
        }
        if (cts is null || run is null) return;
        cts.Cancel();
        try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        cts.Dispose();
        SetState(RendezvousState.Stopped);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Completes when authenticated (Desk ID known). Throws TimeoutException otherwise.</summary>
    public async Task WaitOnlineAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task gate;
        lock (_stateGate) gate = _state == RendezvousState.Online ? Task.CompletedTask : _online.Task;
        try { await gate.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("Rendezvous service not reachable (last: " + (_lastError ?? "none") + ")."); }
    }

    private void SetState(RendezvousState state)
    {
        lock (_stateGate)
        {
            if (_state == state) return;
            _state = state;
            if (state == RendezvousState.Online) _online.TrySetResult();
            else if (_online.Task.IsCompleted) _online = NewGate();
        }
        StateChanged?.Invoke(state);
    }

    // ---------------- connection loop ----------------

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var onlineSince = (Stopwatch?)null;
            var immediate = false;
            TimeSpan? retryAfter = null;
            try
            {
                SetState(RendezvousState.Connecting);
                await ConnectionAsync(ct, () => onlineSince = Stopwatch.StartNew()).ConfigureAwait(false);
                _lastError = "closed";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RendezvousAuthException ex)
            {
                _lastError = ex.Error.ToString();
                retryAfter = ex.RetryAfter;
                if (ex.Error == AuthError.RateLimited) retryAfter ??= ReconnectPolicy.DefaultRateLimited;
                switch (ReconnectPolicy.Decide(ex.Error))
                {
                    case ReconnectAction.Stop:
                        // A retry would fail the same way and only feed the server's per-IP limits: stop and surface it.
                        FailAllPipes();
                        Console.WriteLine($"[Rendezvous] stopped, not retrying: {ex.Error}");
                        SetState(RendezvousState.Failed);
                        return;
                    case ReconnectAction.Reregister:
                        // The service forgot our ID (DB restored) or revoked it: drop it and register a fresh one.
                        if (!string.IsNullOrEmpty(_identity.DeskId))
                        {
                            _identity.SetDeskId(null);
                            immediate = true;
                        }
                        break;
                }
            }
            catch (RendezvousServerException ex)
            {
                _lastError = "server_error_" + ex.Code;
                retryAfter = ex.RetryAfter;
                if (ex.Code == ErrorCode.RateLimited) retryAfter ??= ReconnectPolicy.DefaultRateLimited;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name;
            }

            FailAllPipes();
            if (ct.IsCancellationRequested) break;
            SetState(RendezvousState.Backoff);
            if (onlineSince is { } sw && sw.Elapsed >= BackoffReset) attempt = 0;
            if (immediate) continue;

            var delay = ReconnectPolicy.Delay(attempt, retryAfter, RandomNumberGenerator.GetInt32(-200, 201) / 1000.0);
            attempt++;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetState(RendezvousState.Stopped);
    }

    private async Task ConnectionAsync(CancellationToken ct, Action markOnline)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(HelloTimeout);
            await ws.ConnectAsync(_url, connectTimeout.Token).ConfigureAwait(false);
        }

        SetState(RendezvousState.Authenticating);
        RendezvousFrame hello;
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(HelloTimeout);
            hello = await ReceiveFrameAsync(ws, t.Token).ConfigureAwait(false)
                ?? throw new IOException("closed before hello");
        }
        if (hello.MsgCase == RendezvousFrame.MsgOneofCase.Error) throw ServerError(hello.Error);
        if (hello.MsgCase != RendezvousFrame.MsgOneofCase.ServerHello || hello.ServerHello.Protocol != 1)
            throw new IOException("unexpected hello");

        var deskId = _identity.DeskId ?? string.Empty;
        var spki = _identity.PublicKey;
        var sig = _identity.Sign(IdentityPayloads.RendezvousAuth(hello.ServerHello.Nonce.ToByteArray(), _signedUrl, spki, deskId));
        _ws = ws;
        try
        {
            await SendFrameAsync(new RendezvousFrame
            {
                Seq = NextSeq(),
                ClientAuth = new ClientAuth
                {
                    Spki = ByteString.CopyFrom(spki),
                    DeskId = deskId,
                    Platform = _platform,
                    AppVersion = _appVersion,
                    Sig = ByteString.CopyFrom(sig),
                },
            }, ct).ConfigureAwait(false);

            RendezvousFrame? reply;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(HelloTimeout);
                reply = await ReceiveFrameAsync(ws, t.Token).ConfigureAwait(false);
            }
            if (reply is { MsgCase: RendezvousFrame.MsgOneofCase.Error }) throw ServerError(reply.Error);
            if (reply is null || reply.MsgCase != RendezvousFrame.MsgOneofCase.AuthResult)
                throw new IOException("no auth result");
            if (!reply.AuthResult.Ok)
            {
                // A RATE_LIMITED answer is followed by an Error frame with retry-after; read it if it is already on the way.
                TimeSpan? retryAfter = null;
                if (reply.AuthResult.Error == AuthError.RateLimited)
                {
                    try
                    {
                        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        t.CancelAfter(TimeSpan.FromSeconds(1));
                        var follow = await ReceiveFrameAsync(ws, t.Token).ConfigureAwait(false);
                        if (follow is { MsgCase: RendezvousFrame.MsgOneofCase.Error }) retryAfter = ServerError(follow.Error).RetryAfter;
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
                }
                throw new RendezvousAuthException(reply.AuthResult.Error, retryAfter);
            }
            if (reply.AuthResult.DeskId != _identity.DeskId) _identity.SetDeskId(reply.AuthResult.DeskId);

            markOnline();
            SetState(RendezvousState.Online);
            await ReadLoopAsync(ws, ct).ConfigureAwait(false);
        }
        finally
        {
            _ws = null;
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* socket already gone; nothing to clean up */ }
            }
        }
    }

    private static RendezvousServerException ServerError(Error e) =>
        new(e.Code, e.RetryAfterMs > 0 ? TimeSpan.FromMilliseconds(e.RetryAfterMs) : null);

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (true)
        {
            RendezvousFrame? f;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                silence.CancelAfter(SilenceLimit);
                f = await ReceiveFrameAsync(ws, silence.Token).ConfigureAwait(false);
            }
            if (f is null) return;
            await DispatchAsync(f, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(RendezvousFrame f, CancellationToken ct)
    {
        switch (f.MsgCase)
        {
            case RendezvousFrame.MsgOneofCase.Ping:
                await SendFrameAsync(new RendezvousFrame { Seq = NextSeq(), Pong = new Pong() }, ct).ConfigureAwait(false);
                break;
            case RendezvousFrame.MsgOneofCase.IceConfig:
                IceServers.Update(f.IceConfig);
                break;
            case RendezvousFrame.MsgOneofCase.PairSlotStatus:
                PairSlotStatusChanged?.Invoke(new((int)f.PairSlotStatus.AttemptsLeft, f.PairSlotStatus.Expired));
                break;
            case RendezvousFrame.MsgOneofCase.IncomingPipe:
                OnIncomingPipe(f.IncomingPipe);
                break;
            case RendezvousFrame.MsgOneofCase.PipeOpened:
                OnPipeOpened(f.Seq, f.PipeOpened);
                break;
            case RendezvousFrame.MsgOneofCase.PipeFailed:
                if (_opens.TryRemove(f.Seq, out var failedOpen))
                    failedOpen.TrySetException(new RendezvousPipeException(f.PipeFailed.Reason));
                break;
            case RendezvousFrame.MsgOneofCase.PipeData:
                if (_pipes.TryGetValue(f.PipeData.PipeId, out var pipe))
                    await pipe.EnqueueAsync(f.PipeData.Data.ToByteArray(), ct).ConfigureAwait(false);
                break;
            case RendezvousFrame.MsgOneofCase.PipeClose:
                {
                    var id = f.PipeClose.PipeId;
                    if (_pipes.TryRemove(id, out var closed)) closed.RemoteEnded(null);
                    if (_incoming.TryRemove(id, out var pending))
                        pending.TrySetException(new RendezvousPipeException(PipeFailReason.Unavailable));
                    break;
                }
            case RendezvousFrame.MsgOneofCase.Error:
                _lastError = "server_error_" + f.Error.Code;
                break;
        }
    }

    private void OnIncomingPipe(IncomingPipe frame)
    {
        var opened = new TaskCompletionSource<RelayPipeStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _incoming[frame.PipeId] = opened;
        var request = new IncomingPipeRequest(this, frame, opened);
        var handler = PipeRequested;
        if (handler is null)
        {
            _ = request.DeclineAsync();
            return;
        }
        _ = Task.Run(async () =>
        {
            try { handler(request); }
            catch (Exception) { await request.DeclineAsync().ConfigureAwait(false); }
        });
    }

    private void OnPipeOpened(ulong seq, PipeOpened opened)
    {
        var peer = opened.PeerSpki.ToByteArray();
        if (_incoming.TryRemove(opened.PipeId, out var hostSide))
        {
            var stream = new RelayPipeStream(this, opened.PipeId, peer);
            _pipes[opened.PipeId] = stream;
            hostSide.TrySetResult(stream);
        }
        else if (_opens.TryRemove(seq, out var requesterSide))
        {
            var stream = new RelayPipeStream(this, opened.PipeId, peer);
            _pipes[opened.PipeId] = stream;
            requesterSide.TrySetResult(stream);
        }
        else
        {
            // Nobody waits for it any more (cancelled): close it right away.
            _ = SendFrameAsync(new RendezvousFrame { Seq = NextSeq(), PipeClose = new PipeClose { PipeId = opened.PipeId } }, CancellationToken.None);
        }
    }

    private void FailAllPipes()
    {
        var lost = new IOException("Rendezvous connection lost.");
        foreach (var kv in _pipes)
            if (_pipes.TryRemove(kv.Key, out var p)) p.RemoteEnded(lost);
        foreach (var kv in _opens)
            if (_opens.TryRemove(kv.Key, out var t)) t.TrySetException(lost);
        foreach (var kv in _incoming)
            if (_incoming.TryRemove(kv.Key, out var t)) t.TrySetException(lost);
    }

    // ---------------- public operations ----------------

    /// <summary>Opens a pipe to a host's Desk ID (any format; checksum is verified before any network call).
    /// Throws RendezvousPipeException with the service's reason, or ArgumentException for a malformed ID/code.</summary>
    public async Task<RelayPipeStream> OpenPipeAsync(string targetDeskId, PipePurpose purpose, string? code, CancellationToken ct)
    {
        var parsed = ReyDesk.Windows.Identity.DeskId.Parse(targetDeskId);
        if (!parsed.Ok) throw new ArgumentException("Malformed Desk ID.", nameof(targetDeskId));
        if (purpose == PipePurpose.PairCode)
        {
            var c = RemoteCode.Normalize(code);
            if (!c.Ok) throw new ArgumentException("Malformed remote code.", nameof(code));
            code = c.Canonical;
        }
        else code = null;

        var seq = NextSeq();
        var waiter = new TaskCompletionSource<RelayPipeStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _opens[seq] = waiter;
        try
        {
            await SendFrameAsync(new RendezvousFrame
            {
                Seq = seq,
                OpenPipe = new OpenPipe { TargetDeskId = parsed.Canonical!, Purpose = purpose, Code = code ?? string.Empty },
            }, ct).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (_opens.TryRemove(seq, out _)) { /* late PipeOpened is closed in OnPipeOpened */ }
            throw;
        }
    }

    /// <summary>Creates (or replaces) the single pairing slot for the 6-digit code. The service stores only a salted hash.</summary>
    public Task CreatePairSlotAsync(string code, int ttlSeconds = 300, CancellationToken ct = default)
    {
        var deskId = _identity.DeskId ?? throw new InvalidOperationException("Not registered yet.");
        var c = RemoteCode.Normalize(code);
        if (!c.Ok) throw new ArgumentException("Malformed remote code.", nameof(code));
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = RemoteCode.CodeHash(deskId, c.Canonical!, salt);
        return SendFrameAsync(new RendezvousFrame
        {
            Seq = NextSeq(),
            PairSlotCreate = new PairSlotCreate
            {
                CodeHash = ByteString.CopyFrom(hash),
                Salt = ByteString.CopyFrom(salt),
                TtlS = (uint)Math.Clamp(ttlSeconds, 1, 300),
            },
        }, ct);
    }

    public Task CancelPairSlotAsync(CancellationToken ct = default) =>
        SendFrameAsync(new RendezvousFrame { Seq = NextSeq(), PairSlotCancel = new PairSlotCancel() }, ct);

    private Task RequestIceConfigAsync() =>
        SendFrameAsync(new RendezvousFrame { Seq = NextSeq(), IceConfigRequest = new IceConfigRequest() }, CancellationToken.None);

    // ---------------- internals used by IncomingPipeRequest / RelayPipeStream ----------------

    internal async Task<RelayPipeStream?> DecideAsync(IncomingPipeRequest request, bool accept,
        TaskCompletionSource<RelayPipeStream> opened, CancellationToken ct)
    {
        if (!accept && !_incoming.TryRemove(request.PipeId, out _)) return null;
        await SendFrameAsync(new RendezvousFrame
        {
            Seq = NextSeq(),
            PipeDecision = new PipeDecision { PipeId = request.PipeId, Accept = accept },
        }, ct).ConfigureAwait(false);
        if (!accept) return null;
        return await opened.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    internal Task SendPipeDataAsync(string pipeId, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        SendFrameAsync(new RendezvousFrame
        {
            Seq = NextSeq(),
            PipeData = new PipeData { PipeId = pipeId, Data = UnsafeByteOperations.UnsafeWrap(data) },
        }, ct);

    internal void ClosePipeLocal(string pipeId)
    {
        if (!_pipes.TryRemove(pipeId, out _)) return;
        _ = SendFrameAsync(new RendezvousFrame { Seq = NextSeq(), PipeClose = new PipeClose { PipeId = pipeId } }, CancellationToken.None)
            .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // ---------------- wire ----------------

    private ulong NextSeq() => (ulong)Interlocked.Increment(ref _seq);

    private async Task SendFrameAsync(RendezvousFrame frame, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) throw new IOException("Not connected to the rendezvous service.");
        var bytes = frame.ToByteArray();
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private static async Task<RendezvousFrame?> ReceiveFrameAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            if (r.MessageType != WebSocketMessageType.Binary) throw new IOException("text frame");
            message.Write(buffer, 0, r.Count);
            if (message.Length > MaxFrame) throw new IOException("frame too large");
            if (r.EndOfMessage) break;
        }
        return RendezvousFrame.Parser.ParseFrom(message.GetBuffer(), 0, (int)message.Length);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Signaling;
using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Protocol.V1;
using Google.Protobuf;

namespace ReyDesk.Windows.Pairing;

/// <summary>TLS-only pairing v2 (docs/identity-and-pairing.md section 2). Two transports share one protocol core:
/// the LAN TCP accept loop and <see cref="HandlePipeAsync"/> for a rendezvous relay pipe. Windows is always the TLS server
/// with its identity certificate; the phone pins it. Every enrollment passes a v2 signature check and the on-PC approval
/// gate; reconnects need an exact trusted key (and the per-device flags). Anything unexpected is rejected: there is no
/// v1 or PIN path left, and a v1 request gets "Update ReyDesk on the phone".</summary>
internal sealed class PairingListener : IDisposable
{
    internal const string UpdateReason = "Update ReyDesk on the phone.";
    private const string NotApproved = "Pairing was not approved on the PC.";
    private const string AttendedTimedOut = "Reconnect timed out. Tap Connect again.";
    internal const string CancelledOnPc = "Pairing was cancelled on the PC.";
    internal const string QrAlreadyUsed = "This QR code was already used. Regenerate it on the PC.";
    private static readonly TimeSpan SasConfirmWindow = TimeSpan.FromSeconds(120);
    private const int QrTokenLength = 22;
    private static readonly TimeSpan NewDeviceApproval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AttendedApproval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttendedProofWindow = TimeSpan.FromSeconds(30);

    private TcpListener _listener;
    private readonly TrustedDeviceStore _trustedDevices;
    private readonly Func<PairingOffer?> _currentOfferProvider;
    private readonly IPairingApprovalGate _approvals;
    private readonly Func<string?> _deskIdProvider;
    private readonly X509Certificate2 _certificate;
    private readonly DeviceIdentityService _identity;
    private readonly PairingGate _gate = new();
    private readonly SemaphoreSlim _pending = new(4, 4);
    private readonly SemaphoreSlim _approvalDialog = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private RemoteCodeSlot? _remoteSlot;

    /// <summary>Monotonic milliseconds used for the M5 proof deadline (never wall time). Tests replace it.</summary>
    internal Func<long> MonotonicMs { get; set; } = () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>New primary event (LAN and pipe): the receiver wraps the stream in a SignalingConnection and owns it.</summary>
    public event Action<PairedSession>? Paired;

    /// <summary>Compatibility shim, LAN only, raised when nobody subscribed to <see cref="Paired"/>. T10 removes it.</summary>
    public event Action<SignalingConnection, DeviceIdentity>? DevicePaired;

    public PairingListener(TrustedDeviceStore trustedDevices, Func<PairingOffer?> currentOfferProvider,
        DeviceIdentityService identity, IPairingApprovalGate approvals, Func<string?>? deskIdProvider = null)
    {
        _trustedDevices = trustedDevices;
        _currentOfferProvider = currentOfferProvider;
        _approvals = approvals;
        _deskIdProvider = deskIdProvider ?? (() => null);
        _identity = identity;
        _certificate = identity.CreateTlsCertificate();
        _listener = new TcpListener(IPAddress.Any, PairingOfferFactory.LocalPairingPort);
    }

    /// <summary>Shim for the old callback shape; T10 switches SessionCoordinator to <see cref="IPairingApprovalGate"/>.</summary>
    public PairingListener(TrustedDeviceStore trustedDevices, Func<PairingOffer?> currentOfferProvider,
        DeviceIdentityService identity, Func<DeviceIdentity, Task<bool>> approveNewDevice, Func<string?>? deskIdProvider = null)
        : this(trustedDevices, currentOfferProvider, identity, new LegacyApprovalGate(approveNewDevice), deskIdProvider) { }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    // ---------------- remote code (M4) ----------------

    /// <summary>Arms the host half of the typed code. Replaces and wipes any previous slot. Throws on a malformed code.</summary>
    public void ArmRemoteCode(string code, DateTimeOffset expiresAt)
    {
        var parsed = RemoteCode.Normalize(code);
        if (!parsed.Ok) throw new ArgumentException("Malformed remote code.", nameof(code));
        Interlocked.Exchange(ref _remoteSlot, new RemoteCodeSlot(parsed.Canonical!, expiresAt.ToUnixTimeMilliseconds()))?.Wipe();
    }

    public void DisarmRemoteCode() => Interlocked.Exchange(ref _remoteSlot, null)?.Wipe();

    // ---------------- transports ----------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pairing] Listener failed: {ex.GetType().Name}");
                return;
            }
            if (!_gate.AllowConnection(Now()) || !_pending.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = RunLanAsync(client, ct);
        }
    }

    private async Task RunLanAsync(TcpClient client, CancellationToken ct)
    {
        try { await HandleStreamAsync(client.GetStream(), PairingContext.Lan, client, ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[Pairing] Secure pairing failed: {ex.GetType().Name}. Update both apps and retry."); client.Dispose(); }
        finally { ReleasePending(); }
    }

    /// <summary>Cheap pre-check for T10 before it accepts an <c>IncomingPipeRequest</c>: pairing codes only while the owner has
    /// one open, QR only while an offer is current, reconnects only from a remote-allowed trusted key. Anything else should be
    /// declined without a prompt. The listener re-checks everything inside the stream anyway.</summary>
    public bool ShouldAcceptPipe(PipePurpose purpose, byte[]? requesterFingerprint)
    {
        var now = Now();
        switch (purpose)
        {
            case PipePurpose.PairCode:
                return !_gate.IsSasPaused(now) && Volatile.Read(ref _remoteSlot)?.IsAlive(now) == true;
            case PipePurpose.PairQr:
                return _currentOfferProvider() is { } offer && offer.ExpiresAtUnixMs > now;
            case PipePurpose.Reconnect:
                return requesterFingerprint is { Length: 32 } && _trustedDevices.HasRemoteAllowedFingerprint(requesterFingerprint);
            default:
                return false;
        }
    }

    /// <summary>Runs the pairing protocol over an accepted rendezvous pipe (TLS inside the pipe, same as LAN). Takes ownership
    /// of <paramref name="pipe"/>. Never throws: failures close the pipe.</summary>
    public async Task HandlePipeAsync(Stream pipe, PairingContext context, CancellationToken ct = default)
    {
        if (context.Medium != ConnectionMedium.Remote || !_gate.AllowConnection(Now()) || !_pending.Wait(0))
        {
            Log($"Pipe ({context.Purpose}) dropped before TLS: connection rate limit or too many pairings at once.");
            pipe.Dispose();
            return;
        }
        Log($"Pipe ({context.Purpose}) accepted, starting TLS.");
        try { await HandleStreamAsync(pipe, context, null, ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[Pairing] Secure pairing failed: {ex.GetType().Name}."); pipe.Dispose(); }
        finally { ReleasePending(); }
    }

    private void ReleasePending()
    {
        _pending.Release();
        if (_cts.IsCancellationRequested && _pending.CurrentCount == 4) _certificate.Dispose();
    }

    // ---------------- one connection ----------------

    internal async Task HandleStreamAsync(Stream raw, PairingContext ctx, TcpClient? lan, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        using var cancellation = lifetime.Token.Register(() =>
        {
            try { raw.Dispose(); } catch (Exception) { /* already closed */ }
            try { lan?.Dispose(); } catch (Exception) { /* already closed */ }
        });
        var token = lifetime.Token;
        SslStream? tls = null;
        var handedOff = false;
        try
        {
            if (lan is not null) lan.NoDelay = true;
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(10));
            tls = new SslStream(raw, false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, handshakeCts.Token).ConfigureAwait(false);
            if (tls.CanTimeout) tls.WriteTimeout = 10_000;

            var first = await ReadEnvelopeAsync(tls, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            var request = first?.PayloadCase == Envelope.PayloadOneofCase.PairingRequest ? first.PairingRequest : null;
            var where = ctx.Medium == ConnectionMedium.Remote ? $"internet ({ctx.Purpose})" : "LAN";
            Log($"Attempt received: {request?.Method.ToString() ?? "no request"} over {where}.");
            var decision = await NegotiateAsync(tls, request, ctx, token).ConfigureAwait(false);
            var response = decision.Response;
            var success = response.Result is PairingResult.Accepted or PairingResult.AlreadyTrusted;
            Log($"Result: {request?.Method.ToString() ?? "no request"} over {where} -> {response.Result}" +
                (string.IsNullOrEmpty(response.Reason) ? "." : $" ({response.Reason})"));
            if (success)
            {
                var peer = decision.Request!;
                string? peerDeskId = null;
                if (ctx.Medium == ConnectionMedium.Remote && ctx.RequesterFingerprint is not null &&
                    DeskId.Parse(ctx.RequesterDeskId) is { Ok: true } parsed) peerDeskId = parsed.Canonical;
                _trustedDevices.AddOrUpdate(peer.Requester.DeviceId, peer.Requester.DisplayName, peer.RequesterPublicKey.ToByteArray(),
                    new TrustEnrollment(peerDeskId, decision.RemoteAllowed, (int)decision.Method));
                response.HostDeskId = _deskIdProvider() ?? "";
            }
            EnvelopeFraming.Write(tls, new Envelope
            {
                ProtocolVersion = 1,
                SentAtUnixMs = Now(),
                PairingResponse = response,
            });
            if (!success) return;

            if (tls.CanTimeout) tls.ReadTimeout = Timeout.Infinite;
            var key = decision.Request!.RequesterPublicKey.ToByteArray();
            var owner = new StreamOwner(tls, lan);
            var session = new PairedSession(tls, owner, ctx.Medium, decision.Unattended, key, decision.Request.Requester, decision.Method);
            if (Paired is { } paired)
            {
                paired(session);
                handedOff = true;
            }
            else if (lan is not null && DevicePaired is { } legacy)
            {
                var connection = new SignalingConnection(lan, tls, decision.Unattended, key);
                legacy(connection, decision.Request.Requester);
                connection.StartReadLoop();
                handedOff = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pairing] Secure pairing failed: {ex.GetType().Name}. Update both apps and retry.");
        }
        finally
        {
            if (!handedOff)
            {
                tls?.Dispose();
                try { raw.Dispose(); } catch (Exception) { /* already closed */ }
                lan?.Dispose();
            }
        }
    }

    private sealed class StreamOwner(Stream tls, TcpClient? lan) : IDisposable
    {
        public void Dispose()
        {
            tls.Dispose();
            lan?.Dispose();
        }
    }

    private sealed record Decision(PairingResponse Response, PairingRequest? Request = null,
        PairingMethod Method = PairingMethod.Unspecified, bool Unattended = false, bool? RemoteAllowed = null);

    // ---------------- protocol core ----------------

    private async Task<Decision> NegotiateAsync(SslStream tls, PairingRequest? request, PairingContext ctx, CancellationToken ct)
    {
        if (request?.Requester is null) return Deny("No valid active pairing request.");
        // v1 (no method, PIN, v1 unattended) is a downgrade and is never served.
        if (request.Method == PairingMethod.Unspecified) return Deny(UpdateReason);
        if (!MethodAllowed(request.Method, ctx) ||
            request.UnattendedReconnect != (request.Method == PairingMethod.TrustedUnattended) ||
            request.Requester.DeviceId.Length is < 1 or > 128 || request.Requester.DisplayName.Length is < 1 or > 128 ||
            request.RequesterPublicKey.Length is < 32 or > 256)
            return Deny("Invalid pairing request or code.");
        var clientKey = request.RequesterPublicKey.ToByteArray();
        var hostDeskId = "";
        if (ctx.Medium == ConnectionMedium.Remote)
        {
            hostDeskId = _deskIdProvider() ?? "";
            if (hostDeskId.Length == 0) return Deny("Internet access is not set up on this PC.");
            // The server reports who it thinks is calling; the key actually used must be that device's key.
            if (ctx.RequesterFingerprint is not null &&
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(clientKey), ctx.RequesterFingerprint))
                return Deny("Invalid pairing request or code.");
        }
        return request.Method switch
        {
            PairingMethod.LanQr or PairingMethod.RemoteQr => await QrFlowAsync(tls, request, clientKey, ctx, hostDeskId, ct).ConfigureAwait(false),
            PairingMethod.LanSas or PairingMethod.RemoteCode => await SasFlowAsync(tls, request, clientKey, ctx, hostDeskId, ct).ConfigureAwait(false),
            PairingMethod.TrustedAttended => await AttendedFlowAsync(tls, request, clientKey, ctx, hostDeskId, ct).ConfigureAwait(false),
            _ => await UnattendedFlowAsync(tls, request, clientKey, ctx, ct).ConfigureAwait(false),
        };
    }

    private static bool MethodAllowed(PairingMethod method, PairingContext ctx) => ctx.Medium switch
    {
        ConnectionMedium.Local => method is PairingMethod.LanQr or PairingMethod.LanSas or PairingMethod.TrustedAttended or PairingMethod.TrustedUnattended,
        ConnectionMedium.Remote => ctx.Purpose switch
        {
            PipePurpose.PairCode => method == PairingMethod.RemoteCode,
            PipePurpose.PairQr => method == PairingMethod.RemoteQr,
            PipePurpose.Reconnect => method is PairingMethod.TrustedAttended or PairingMethod.TrustedUnattended,
            _ => false,
        },
        _ => false,
    };

    // M1 / M3: QR token ---------------------------------------------------------------------------------------

    private async Task<Decision> QrFlowAsync(SslStream tls, PairingRequest request, byte[] clientKey, PairingContext ctx,
        string hostDeskId, CancellationToken ct)
    {
        var offer = _currentOfferProvider();
        if (offer is null || !_gate.TryAttempt(offer))
            return Deny(offer is not null && _gate.IsConsumed(offer) ? QrAlreadyUsed : "Too many attempts with this QR code. Regenerate it on the PC.");
        if (offer.ExpiresAtUnixMs <= Now())
            return new Decision(new PairingResponse { Result = PairingResult.ExpiredToken, Reason = "Pairing offer expired." });
        if (request.Stage != PairingStage.Unspecified || request.Signature.Length is < 8 or > 144 ||
            request.PairingToken.Length != QrTokenLength || offer.PairingToken.Length != QrTokenLength ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.PairingToken), System.Text.Encoding.UTF8.GetBytes(offer.PairingToken)))
            return Deny("Invalid pairing request or code.");
        var payload = PairingSignature.BuildSignedPayloadV2((int)request.Method, request.PairingToken, _identity.PublicKey, clientKey,
            request.Requester.DeviceId, hostDeskId, null, null);
        if (!DeviceIdentityService.Verify(clientKey, payload, request.Signature.ToByteArray())) return Deny("Device identity proof failed.");
        var id = request.Requester.DeviceId;
        var known = _trustedDevices.IsTrusted(id);
        if (known && !_trustedDevices.IsTrusted(id, clientKey)) return Deny("Stored device key differs. Forget the device and pair deliberately again.");
        // A remote path always needs the owner's click unless this key is already allowed over the internet.
        var needsApproval = !known || (ctx.Medium == ConnectionMedium.Remote && !_trustedDevices.IsRemoteAllowed(id, clientKey));
        // Only an enrollment (new device or new remote permission) burns the single-use token. A phone already trusted for this
        // medium proves nothing new with it, so the QR stays usable (its 5-attempt budget and expiry still apply) and a failed
        // session afterwards does not force a Regenerate.
        if (needsApproval && !_gate.TryConsume(offer)) return Deny(QrAlreadyUsed);

        bool? allowRemote = null;
        if (needsApproval)
        {
            var result = await AskOwnerAsync(tls, request, clientKey, ctx, null, known, NewDeviceApproval, ct).ConfigureAwait(false);
            if (!result.Approved) return Deny(NotApproved);
            allowRemote = ctx.Medium == ConnectionMedium.Remote || result.AllowRemote;
        }
        if (!ReferenceEquals(offer, _currentOfferProvider()) || offer.ExpiresAtUnixMs <= Now())
            return Deny("Pairing code changed or expired during approval.");
        if (known && !_trustedDevices.IsTrusted(id, clientKey)) return Deny("Device trust was revoked during pairing.");
        return new Decision(new PairingResponse { Result = known ? PairingResult.AlreadyTrusted : PairingResult.Accepted },
            request, request.Method, false, allowRemote);
    }

    // M2 / M4: SAS commit-then-reveal ---------------------------------------------------------------------------

    private async Task<Decision> SasFlowAsync(SslStream tls, PairingRequest first, byte[] clientKey, PairingContext ctx,
        string hostDeskId, CancellationToken ct)
    {
        var method = first.Method;
        var id = first.Requester.DeviceId;
        if (_gate.IsSasPaused(Now())) return Deny("Code comparison is paused after failed attempts. Try again in a few minutes.");
        PairingOffer? offer = null;
        RemoteCodeSlot? slot = null;
        if (method == PairingMethod.LanSas)
        {
            // Nearby pairing is only open while the Connection panel shows a current offer.
            offer = _currentOfferProvider();
            if (offer is null || offer.ExpiresAtUnixMs <= Now()) return Deny("Open the Connection panel on the PC and try again.");
        }
        else
        {
            slot = Volatile.Read(ref _remoteSlot);
            if (slot is null || !slot.IsAlive(Now())) return Deny("The internet pairing code is not active. Create a new one on the PC.");
        }
        if (first.Stage != PairingStage.Unspecified || first.SasCommitment.Length != 32 || first.SasClientNonce.Length != 0)
            return Deny("Invalid pairing request or code.");

        var commitment = first.SasCommitment.ToByteArray();
        var hostNonce = Sas.RandomNonce();
        byte[]? clientNonce = null;
        try
        {
            // The host nonce is sent only after the commitment arrived.
            WriteResponse(tls, new PairingResponse { Stage = PairingStage.SasHostNonce, SasHostNonce = ByteString.CopyFrom(hostNonce) });
            var reveal = await ReadRequestAsync(tls, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (reveal is null || reveal.Stage != PairingStage.SasReveal || reveal.Method != method ||
                !SameDevice(reveal, id, clientKey) || reveal.SasClientNonce.Length != 32)
                return Deny("Invalid pairing request or code.");
            clientNonce = reveal.SasClientNonce.ToByteArray();
            if (!Sas.VerifyCommitment(commitment, clientNonce))
            {
                _gate.RecordSasFailure(Now());
                return Deny("Code comparison failed.");
            }
            // Hold the SAS only as long as the owner dialogs need it.
            var sas = Sas.Display(PairingSignature.ComputeSas(_identity.PublicKey, clientKey, hostNonce, clientNonce));

            // Both screens show the SAS before the phone confirms (spec 2.2 step 4). Before 2026-11 the PC showed it only after
            // the phone's confirm, so the phone asked "same code?" while the PC showed nothing.
            // The compare dialog trusts nothing: the owner's Approve still comes after confirm, code check and signature.
            PairingRequest? confirm;
            using (var compareCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var compareRequest = new PairingApprovalRequest(method, ShownIdentity(first), KeyId.FromSpki(clientKey), sas, ctx.Medium,
                    ctx.Medium == ConnectionMedium.Remote, _trustedDevices.IsTrusted(id), SasConfirmWindow, CompareOnly: true);
                var compare = _approvals.ShowComparisonAsync(compareRequest, compareCts.Token);
                Log($"{method} over {ctx.Medium}: code comparison shown on the PC, waiting for the phone");
                try
                {
                    var read = ReadRequestAsync(tls, SasConfirmWindow, ct);
                    if (await Task.WhenAny(read, compare).ConfigureAwait(false) == compare && await compare.ConfigureAwait(false))
                    {
                        _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        _gate.RecordSasFailure(Now());
                        return Deny(CancelledOnPc);
                    }
                    confirm = await read.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _gate.RecordSasFailure(Now()); // walked away after the reveal: counts like a mismatch
                    Log($"{method}: no answer from the phone after the code comparison ({ex.GetType().Name})");
                    throw;
                }
                finally
                {
                    compareCts.Cancel();
                    try { await compare.ConfigureAwait(false); } catch (OperationCanceledException) { /* closed */ }
                }
            }
            if (confirm is null) { _gate.RecordSasFailure(Now()); return Deny("Codes were not confirmed."); }
            if (confirm.Stage != PairingStage.SasConfirm || confirm.Method != method || !SameDevice(confirm, id, clientKey) ||
                confirm.Signature.Length is < 8 or > 144)
                return Deny("Invalid pairing request or code.");

            var tokenForSignature = "";
            if (method == PairingMethod.RemoteCode)
            {
                var code = RemoteCode.Normalize(confirm.PairingToken);
                var check = slot!.Check(code.Ok ? code.Canonical! : "", Now());
                if (check == RemoteCodeCheck.Dead) return Deny("The internet pairing code is not active. Create a new one on the PC.");
                if (!code.Ok || check != RemoteCodeCheck.Ok) return Deny("Invalid pairing request or code.");
                tokenForSignature = code.Canonical!;
            }
            else if (confirm.PairingToken.Length != 0) return Deny("Invalid pairing request or code.");

            var payload = PairingSignature.BuildSignedPayloadV2((int)method, tokenForSignature, _identity.PublicKey, clientKey, id,
                hostDeskId, hostNonce, clientNonce);
            if (!DeviceIdentityService.Verify(clientKey, payload, confirm.Signature.ToByteArray())) return Deny("Device identity proof failed.");

            var known = _trustedDevices.IsTrusted(id);
            if (known && !_trustedDevices.IsTrusted(id, clientKey)) return Deny("Stored device key differs. Forget the device and pair deliberately again.");
            if (slot is not null && !slot.TryConsume(Now())) return Deny("The internet pairing code is not active. Create a new one on the PC.");

            var result = await AskOwnerAsync(tls, confirm, clientKey, ctx, sas, known, NewDeviceApproval, ct).ConfigureAwait(false);
            if (!result.Approved)
            {
                _gate.RecordSasFailure(Now());
                return Deny(NotApproved);
            }
            if (method == PairingMethod.LanSas && (_currentOfferProvider() is not { } current || current.ExpiresAtUnixMs <= Now()))
                return Deny("Pairing was closed on the PC during approval.");
            if (known && !_trustedDevices.IsTrusted(id, clientKey)) return Deny("Device trust was revoked during pairing.");
            return new Decision(new PairingResponse { Result = known ? PairingResult.AlreadyTrusted : PairingResult.Accepted },
                confirm, method, false, ctx.Medium == ConnectionMedium.Remote || result.AllowRemote);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostNonce);
            CryptographicOperations.ZeroMemory(commitment);
            if (clientNonce is not null) CryptographicOperations.ZeroMemory(clientNonce);
        }
    }

    // M5: trusted attended reconnect with a fresh host nonce (spec 2.8, attended v3) ----------------------------

    /// <summary>Failed-attempt budget per peer key fingerprint (5 per 10 min, then rejected before any nonce or prompt). The key is
    /// always present on an M5 request, so no IP or Desk ID fallback is needed. A success clears the budget.</summary>
    private async Task<Decision> AttendedFlowAsync(SslStream tls, PairingRequest request, byte[] clientKey, PairingContext ctx,
        string hostDeskId, CancellationToken ct)
    {
        var peer = Convert.ToHexString(SHA256.HashData(clientKey));
        if (_gate.IsPeerBlocked(peer, MonotonicMs()))
            return Deny("Too many failed reconnect attempts. Try again in a few minutes.");
        Decision decision;
        try { decision = await AttendedCoreAsync(tls, request, clientKey, ctx, hostDeskId, ct).ConfigureAwait(false); }
        catch (Exception) { _gate.RecordPeerFailure(peer, MonotonicMs()); throw; }
        if (decision.Response.Result == PairingResult.AlreadyTrusted) _gate.ClearPeerFailures(peer);
        else _gate.RecordPeerFailure(peer, MonotonicMs());
        return decision;
    }

    private async Task<Decision> AttendedCoreAsync(SslStream tls, PairingRequest request, byte[] clientKey, PairingContext ctx,
        string hostDeskId, CancellationToken ct)
    {
        var id = request.Requester.DeviceId;
        // The old one-shot request carried a signature and no nonce: never served, never challenged.
        if (request.AttendedClientNonce.Length == 0 && request.Signature.Length != 0) return Deny(UpdateReason);
        if (request.Stage != PairingStage.Unspecified || request.PairingToken.Length != 0 || request.Signature.Length != 0 ||
            request.SasCommitment.Length != 0 || request.SasClientNonce.Length != 0 || request.ReconnectClientNonce.Length != 0 ||
            request.AttendedClientNonce.Length != 32)
            return Deny("Invalid pairing request or code.");
        if (!_trustedDevices.IsTrusted(id, clientKey)) return Deny("This PC does not trust this phone. Pair it again.");
        if (ctx.Medium == ConnectionMedium.Remote && !_trustedDevices.IsRemoteAllowed(id, clientKey))
            return Deny("Allow over internet is off for this phone. Enable it on the PC.");
        // Both Desk IDs are empty on the LAN and both canonical over a pipe; anything else is rejected before a nonce exists.
        var signedHostDeskId = "";
        var signedRequesterDeskId = "";
        if (ctx.Medium == ConnectionMedium.Remote)
        {
            var ownDesk = DeskId.Parse(hostDeskId);
            var peerDesk = DeskId.Parse(ctx.RequesterDeskId);
            if (!ownDesk.Ok || !peerDesk.Ok) return Deny("Internet access is not set up for this connection. Try again.");
            signedHostDeskId = ownDesk.Canonical!;
            signedRequesterDeskId = peerDesk.Canonical!;
        }

        var nc = request.AttendedClientNonce.ToByteArray();
        var nh = RandomNumberGenerator.GetBytes(32);
        try
        {
            var deadline = MonotonicMs() + (long)AttendedProofWindow.TotalMilliseconds;
            WriteResponse(tls, new PairingResponse { Stage = PairingStage.AttendedChallenge, AttendedHostNonce = ByteString.CopyFrom(nh) });

            var remaining = deadline - MonotonicMs();
            if (remaining <= 0) return Deny(AttendedTimedOut);
            PairingRequest? proof;
            try { proof = await ReadRequestAsync(tls, TimeSpan.FromMilliseconds(remaining), ct).ConfigureAwait(false); }
            catch (TimeoutException) { return Deny(AttendedTimedOut); }
            // nh exists only in this method: this single read consumes it, and the finally below zeroes it on every path.
            if (MonotonicMs() >= deadline) return Deny(AttendedTimedOut);
            if (proof is null || proof.Stage != PairingStage.AttendedProof || proof.Method != PairingMethod.TrustedAttended ||
                !SameDevice(proof, id, clientKey) || proof.PairingToken.Length != 0 || proof.AttendedClientNonce.Length != 0 ||
                proof.ReconnectClientNonce.Length != 0 || proof.SasCommitment.Length != 0 || proof.SasClientNonce.Length != 0 ||
                proof.UnattendedReconnect || proof.Signature.Length is < 8 or > 144)
                return Deny("Invalid pairing request or code.");
            var payload = PairingSignature.BuildAttendedV3(_identity.PublicKey, clientKey, id, signedHostDeskId, signedRequesterDeskId, nh, nc);
            if (!DeviceIdentityService.Verify(clientKey, payload, proof.Signature.ToByteArray())) return Deny("Device identity proof failed.");

            // Only a verified, fresh proof reaches the owner.
            var result = await AskOwnerAsync(tls, proof, clientKey, ctx, null, true, AttendedApproval, ct).ConfigureAwait(false);
            if (!result.Approved) return Deny("The connection was declined on the PC.");
            if (!_trustedDevices.IsTrusted(id, clientKey)) return Deny("Device trust was revoked during pairing.");
            if (ctx.Medium == ConnectionMedium.Remote && !_trustedDevices.IsRemoteAllowed(id, clientKey))
                return Deny("Allow over internet was turned off during the request.");
            return new Decision(new PairingResponse { Result = PairingResult.AlreadyTrusted }, request, PairingMethod.TrustedAttended);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nh);
            CryptographicOperations.ZeroMemory(nc);
        }
    }

    // M6: trusted unattended reconnect ---------------------------------------------------------------------------

    private async Task<Decision> UnattendedFlowAsync(SslStream tls, PairingRequest request, byte[] clientKey, PairingContext ctx,
        CancellationToken ct)
    {
        var id = request.Requester.DeviceId;
        var connection = new object(); // budget and single use are per connection, never tied to the displayed QR offer
        bool Allowed() => _trustedDevices.IsUnattendedAllowed(id, clientKey) &&
            (ctx.Medium != ConnectionMedium.Remote || _trustedDevices.IsRemoteAllowed(id, clientKey));
        if (!_gate.TryAttempt(connection) || request.Stage != PairingStage.Unspecified || request.ReconnectClientNonce.Length != 32 ||
            request.Signature.Length != 0 || request.PairingToken.Length != 0 || !Allowed())
            return Deny("Enable unattended access (and Allow over internet for remote use) for this paired device on the PC, or pair it again.");
        var challenge = RandomNumberGenerator.GetBytes(32);
        var clientNonce = request.ReconnectClientNonce.ToByteArray();
        var expiresAt = Now() + 60_000;
        var hostPayload = PairingSignature.BuildReconnectPayload(true, challenge, clientNonce,
            _identity.DeviceId, id, _identity.PublicKey, clientKey, expiresAt);
        WriteResponse(tls, new PairingResponse
        {
            ReconnectChallenge = ByteString.CopyFrom(challenge),
            ReconnectHostSignature = ByteString.CopyFrom(_identity.Sign(hostPayload)),
            ReconnectExpiresAtUnixMs = expiresAt,
        });
        var proof = await ReadRequestAsync(tls, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (proof is null || !proof.UnattendedReconnect || proof.Method != PairingMethod.TrustedUnattended || proof.Requester is null ||
            proof.Requester.DeviceId != id || proof.PairingToken.Length != 0 || proof.Signature.Length is < 8 or > 144 ||
            !CryptographicOperations.FixedTimeEquals(proof.RequesterPublicKey.ToByteArray(), clientKey) ||
            !CryptographicOperations.FixedTimeEquals(proof.ReconnectClientNonce.ToByteArray(), clientNonce) ||
            Now() >= expiresAt || !Allowed())
            return Deny("Reconnect proof expired or access was revoked.");
        var clientPayload = PairingSignature.BuildReconnectPayload(false, challenge, clientNonce,
            _identity.DeviceId, id, _identity.PublicKey, clientKey, expiresAt);
        if (!DeviceIdentityService.Verify(clientKey, clientPayload, proof.Signature.ToByteArray()) || !_gate.TryConsume(connection))
            return Deny("Reconnect device proof failed.");
        return new Decision(new PairingResponse { Result = PairingResult.AlreadyTrusted }, request, PairingMethod.TrustedUnattended, true);
    }

    // ---------------- helpers ----------------

    /// <summary>Tells the phone the owner is being asked, then waits for the on-PC gate (one dialog at a time, timeout enforced).
    /// Declined, busy and timed out all return Declined.</summary>
    private async Task<PairingApprovalResult> AskOwnerAsync(SslStream tls, PairingRequest request, byte[] clientKey, PairingContext ctx,
        string? sas, bool alreadyTrusted, TimeSpan timeout, CancellationToken ct)
    {
        if (!_approvalDialog.Wait(0))
        {
            Log($"{request.Method}: another approval dialog is open, declined.");
            return PairingApprovalResult.Declined;
        }
        Log($"{request.Method}: asking the owner on the PC.");
        try
        {
            WriteResponse(tls, new PairingResponse { Stage = PairingStage.AwaitingApproval });
            var id = request.Requester.DeviceId;
            var remote = ctx.Medium == ConnectionMedium.Remote;
            var approval = new PairingApprovalRequest(request.Method, ShownIdentity(request), KeyId.FromSpki(clientKey), sas, ctx.Medium,
                remote || (alreadyTrusted && _trustedDevices.IsRemoteAllowed(id, clientKey)), alreadyTrusted, timeout);
            using var dialog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialog.CancelAfter(timeout);
            try { return await _approvals.RequestApprovalAsync(approval, dialog.Token).WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { dialog.Cancel(); return PairingApprovalResult.Declined; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return PairingApprovalResult.Declined; }
        }
        finally { _approvalDialog.Release(); }
    }

    private static DeviceIdentity ShownIdentity(PairingRequest request) => new()
    {
        DeviceId = request.Requester.DeviceId, DisplayName = request.Requester.DisplayName, Platform = request.Requester.Platform,
        AppVersion = request.Requester.AppVersion,
    };

    /// <summary>Pairing diagnostics for app.log (Console is redirected there). Methods, media, results and fixed reasons only:
    /// never codes, tokens, nonces, keys, signatures or the SAS.</summary>
    private static void Log(string line) => Console.WriteLine($"[Pairing] {DateTimeOffset.UtcNow:HH:mm:ss}Z {line}");

    private static bool SameDevice(PairingRequest request, string deviceId, byte[] clientKey) =>
        request.Requester is not null && request.Requester.DeviceId == deviceId &&
        CryptographicOperations.FixedTimeEquals(request.RequesterPublicKey.ToByteArray(), clientKey);

    private static async Task<Envelope?> ReadEnvelopeAsync(Stream stream, TimeSpan timeout, CancellationToken ct) =>
        await Task.Run(() => EnvelopeFraming.Read(stream), ct).WaitAsync(timeout, ct).ConfigureAwait(false);

    private static async Task<PairingRequest?> ReadRequestAsync(Stream stream, TimeSpan timeout, CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(stream, timeout, ct).ConfigureAwait(false);
        return envelope?.PayloadCase == Envelope.PayloadOneofCase.PairingRequest ? envelope.PairingRequest : null;
    }

    private static void WriteResponse(Stream stream, PairingResponse response) =>
        EnvelopeFraming.Write(stream, new Envelope { ProtocolVersion = 1, SentAtUnixMs = Now(), PairingResponse = response });

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Decision Deny(string reason) => new(new PairingResponse { Result = PairingResult.Rejected, Reason = reason });

    public void Dispose()
    {
        _cts.Cancel();
        DisarmRemoteCode();
        _listener.Stop();
        if (_pending.CurrentCount == 4) _certificate.Dispose();
    }
}

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Signaling;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    Console.WriteLine("PASS " + label);
    checks++;
}
void Reject(Action action, string label)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { Check(true, label); return; }
    throw new Exception("Accepted invalid data: " + label);
}
var gate = new PairingGate();
var gateOffer = new object();
for (var i = 0; i < 5; i++) Check(gate.TryAttempt(gateOffer), "bounded attempt accepted " + i);
Check(!gate.TryAttempt(gateOffer), "sixth attempt rejected");
var fresh = new object();
Check(gate.TryAttempt(fresh), "new offer resets attempt budget");
var winners = 0;
Parallel.For(0, 100, _ => { if (gate.TryConsume(fresh)) Interlocked.Increment(ref winners); });
Check(winners == 1, "exactly one concurrent challenge consumer");
Check(!gate.TryAttempt(fresh) && !gate.TryConsume(gateOffer), "used and stale challenge rejected");
Check(gate.TryAttempt(new object()) && !gate.TryAttempt(fresh), "new offer cannot reset already consumed challenge on a racing old request");
for (var i = 0; i < 20; i++) Check(gate.AllowConnection(0), "bounded connection admitted " + i);
Check(!gate.AllowConnection(1), "global connection rate limited");
Check(gate.AllowConnection(60000), "connection budget expires");

using var validFrame = new MemoryStream();
EnvelopeFraming.Write(validFrame, new Envelope { ProtocolVersion = 1, SessionId = "test", SessionAnswer = new SessionAnswer { Sdp = "real-answer" } });
validFrame.Position = 0;
Check(EnvelopeFraming.Read(validFrame)?.SessionAnswer.Sdp == "real-answer", "bounded protobuf frame roundtrip");
Check(EnvelopeFraming.Read(validFrame) is null, "clean EOF");
Reject(() => EnvelopeFraming.Read(new MemoryStream(new byte[] { 0x80 })), "truncated varint rejected");
Reject(() => EnvelopeFraming.Read(new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f })), "overflow varint rejected");
Reject(() => EnvelopeFraming.Read(new MemoryStream(new byte[] { 0x81, 0x80, 0x10 })), "oversized frame rejected before allocation");
Reject(() => EnvelopeFraming.Read(new MemoryStream(new byte[] { 4, 8, 1 })), "truncated payload rejected");
Reject(() => EnvelopeFraming.Read(new MemoryStream(new byte[] { 2, 8, 2 })), "unknown protocol rejected");
Reject(() => EnvelopeFraming.Write(new MemoryStream(), new Envelope { ProtocolVersion = 1, SessionId = new string('x', EnvelopeFraming.MaximumBytes) }), "oversized send rejected");


// ---------------- identity, v2 signatures, trust store ----------------
using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var host = (DeviceIdentityService)Activator.CreateInstance(typeof(DeviceIdentityService), BindingFlags.NonPublic | BindingFlags.Instance,
    null, new object[] { "host", "Test Host", hostKey.ExportSubjectPublicKeyInfo(), hostKey.ExportPkcs8PrivateKey() }, null)!;
using var certificate = host.CreateTlsCertificate();
Check(CryptographicOperations.FixedTimeEquals(host.PublicKey, certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo()), "TLS certificate uses existing identity SPKI");
var hostDeskId = DeskId.Generate();
var peerDeskId = DeskId.Generate();
var qrToken = QrToken.Generate();
Check(qrToken.Length == 22, "QR token is 22 base64url characters");
Check(PairingOfferFactory.GenerateQrToken().Length == 22 && PairingOfferFactory.GenerateRemoteCode().Length == 6, "offer factory token and code lengths");

var p0 = new Phone("phone", "Phone");
byte[] SignV2(Phone p, PairingMethod m, string token, string deskId, byte[]? nh, byte[]? nc) =>
    p.Key.SignData(PairingSignature.BuildSignedPayloadV2((int)m, token, host.PublicKey, p.Spki, p.Id, deskId, nh, nc),
        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
var sig = SignV2(p0, PairingMethod.LanQr, qrToken, "", null, null);
bool VerifyV2(Phone p, byte[] signature, PairingMethod m, string token, byte[] hostSpki, string deskId, byte[]? nh = null, byte[]? nc = null) =>
    DeviceIdentityService.Verify(p.Spki, PairingSignature.BuildSignedPayloadV2((int)m, token, hostSpki, p.Spki, p.Id, deskId, nh, nc), signature);
Check(VerifyV2(p0, sig, PairingMethod.LanQr, qrToken, host.PublicKey, ""), "Android DER v2 client proof accepted");
Check(!VerifyV2(p0, sig, PairingMethod.LanQr, QrToken.Generate(), host.PublicKey, ""), "changed token invalidates client proof");
Check(!VerifyV2(p0, sig, PairingMethod.LanQr, qrToken, p0.Spki, ""), "changed host key invalidates client proof");
Check(!VerifyV2(p0, sig, PairingMethod.RemoteQr, qrToken, host.PublicKey, ""), "changed method invalidates client proof (no cross-method replay)");
Check(!VerifyV2(p0, sig, PairingMethod.LanQr, qrToken, host.PublicKey, hostDeskId), "LAN proof cannot be replayed to a Desk ID pipe");
Check(!VerifyV2(p0, sig, PairingMethod.LanQr, qrToken, host.PublicKey, "", RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)), "nonces are bound into the proof");

var temp = Path.Combine(Path.GetTempPath(), "reydesk-security-" + Guid.NewGuid() + ".json");
try
{
    var store = TrustedDeviceStore.LoadOrCreate(temp);
    store.AddOrUpdate("phone", "Phone", p0.Spki);
    Check(store.IsTrusted("phone", p0.Spki), "stored device key accepted");
    Check(!store.IsTrusted("phone", host.PublicKey), "same device ID with substituted key rejected");
    try { store.AddOrUpdate("phone", "Phone", host.PublicKey); throw new Exception("Key overwritten"); }
    catch (InvalidOperationException) { Check(true, "stored key cannot be silently overwritten"); }
    store.Remove("phone");
    Check(!store.IsTrusted("phone", p0.Spki), "revoked device rejected");
}
finally { if (File.Exists(temp)) File.Delete(temp); }

// ---------------- real TLS handshake with SPKI pinning ----------------
async Task TlsRoundTrip(bool useWrongPin)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var client = new TcpClient();
    var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    using var accepted = await listener.AcceptTcpClientAsync();
    await connect;
    listener.Stop();
    using var serverTls = new SslStream(accepted.GetStream());
    using var clientTls = new SslStream(client.GetStream(), false, (_, peer, _, _) =>
    {
        using var parsed = new X509Certificate2(peer!);
        return CryptographicOperations.FixedTimeEquals(useWrongPin ? p0.Spki : host.PublicKey, parsed.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
    });
    var serverHandshake = serverTls.AuthenticateAsServerAsync(certificate, false, System.Security.Authentication.SslProtocols.Tls12, false);
    var clientHandshake = clientTls.AuthenticateAsClientAsync("ReyDesk");
    try { await Task.WhenAll(serverHandshake, clientHandshake).WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception)
    {
        if (useWrongPin && clientHandshake.IsFaulted) { Check(true, "TLS rejects substituted host SPKI"); return; }
        throw;
    }
    if (useWrongPin) throw new Exception("TLS accepted wrong host key");
    EnvelopeFraming.Write(clientTls, new Envelope { ProtocolVersion = 1, SessionId = "encrypted" });
    Check(EnvelopeFraming.Read(serverTls)?.SessionId == "encrypted", "actual pinned TLS loopback exchanges encrypted protobuf");
}
await TlsRoundTrip(false);
await TlsRoundTrip(true);

// ---------------- pairing v2 harness ----------------
PairingOffer NewOffer(int minutes = 5, string? token = null) => new()
{
    Host = new DeviceIdentity { DeviceId = host.DeviceId, DeskId = hostDeskId },
    HostPublicKey = ByteString.CopyFrom(host.PublicKey), PairingToken = token ?? QrToken.Generate(),
    ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds(),
};
PairingContext Remote(Phone p, PipePurpose purpose, string? requesterDeskId = null) =>
    new(ConnectionMedium.Remote, purpose, SHA256.HashData(p.Spki), requesterDeskId ?? peerDeskId);

var cleanup = new List<string>();
var liveSessions = new List<PairedSession>();
Rig NewRig(PairingOffer? offer, string? deskId, Func<PairingApprovalRequest, CancellationToken, Task<PairingApprovalResult>>? handler = null)
{
    var path = Path.Combine(Path.GetTempPath(), "reydesk-v2-" + Guid.NewGuid() + ".json");
    cleanup.Add(path);
    var rig = new Rig { Store = TrustedDeviceStore.LoadOrCreate(path), Offer = offer };
    rig.Handler = handler ?? ((_, _) => Task.FromResult(new PairingApprovalResult(true)));
    rig.Listener = new PairingListener(rig.Store, () => rig.Offer, host, new StubGate(rig), () => deskId);
    rig.Listener.Paired += session => { lock (liveSessions) liveSessions.Add(session); rig.Sessions.Add(session); };
    return rig;
}

async Task<(SslStream Tls, Task Server)> Open(Rig rig, PairingContext ctx)
{
    var tcp = new TcpListener(IPAddress.Loopback, 0);
    tcp.Start();
    var client = new TcpClient();
    var connect = client.ConnectAsync((IPEndPoint)tcp.LocalEndpoint);
    var accepted = await tcp.AcceptTcpClientAsync();
    await connect;
    tcp.Stop();
    var server = rig.Listener.HandleStreamAsync(accepted.GetStream(), ctx, null, CancellationToken.None);
    var tls = new SslStream(client.GetStream(), false, (_, peer, _, _) =>
    {
        using var parsed = new X509Certificate2(peer!);
        return CryptographicOperations.FixedTimeEquals(host.PublicKey, parsed.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
    });
    await tls.AuthenticateAsClientAsync("ReyDesk").WaitAsync(TimeSpan.FromSeconds(5));
    tls.ReadTimeout = 10_000;
    return (tls, server);
}
PairingResponse? Recv(Stream s)
{
    try { return EnvelopeFraming.Read(s)?.PairingResponse; }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { return null; }
}
PairingResponse? RecvFinal(Stream s)
{
    while (true)
    {
        var r = Recv(s);
        if (r is null || r.Stage != PairingStage.AwaitingApproval) return r;
    }
}
void Send(Stream s, PairingRequest r) => EnvelopeFraming.Write(s, new Envelope { ProtocolVersion = 1, PairingRequest = r });
PairingRequest Req(Phone p, PairingMethod m) => new()
{
    Requester = new DeviceIdentity { DeviceId = p.Id, DisplayName = p.Name }, RequesterPublicKey = ByteString.CopyFrom(p.Spki), Method = m,
};
Task Done(Task server) => server.WaitAsync(TimeSpan.FromSeconds(10));

async Task<PairingResponse?> QrPair(Rig rig, Phone p, PairingContext ctx, PairingMethod m, string token, string signedDeskId,
    Action<PairingRequest>? tweak = null)
{
    var (tls, server) = await Open(rig, ctx);
    using (tls)
    {
        var r = Req(p, m);
        r.PairingToken = token;
        r.Signature = ByteString.CopyFrom(SignV2(p, m, token, signedDeskId, null, null));
        tweak?.Invoke(r);
        Send(tls, r);
        var fin = RecvFinal(tls);
        await Done(server);
        return fin;
    }
}

// Full SAS exchange. mode: "ok", "badreveal" (reveal does not match commitment), "walkaway" (drop after reveal).
async Task<(PairingResponse? Final, string? PhoneSas)> SasPair(Rig rig, Phone p, PairingContext ctx, PairingMethod m, string code,
    string signedDeskId, string mode = "ok")
{
    var (tls, server) = await Open(rig, ctx);
    using (tls)
    {
        var nc = Sas.RandomNonce();
        var first = Req(p, m);
        first.SasCommitment = ByteString.CopyFrom(Sas.Commitment(nc));
        Send(tls, first);
        var hn = Recv(tls);
        if (hn is null || hn.Stage != PairingStage.SasHostNonce) { await Done(server); return (hn, null); }
        var nh = hn.SasHostNonce.ToByteArray();
        var reveal = Req(p, m);
        reveal.Stage = PairingStage.SasReveal;
        reveal.SasClientNonce = ByteString.CopyFrom(mode == "badreveal" ? Sas.RandomNonce() : nc);
        Send(tls, reveal);
        if (mode == "badreveal") { var f = RecvFinal(tls); await Done(server); return (f, null); }
        if (mode == "walkaway") { tls.Dispose(); await Done(server); return (null, null); }
        var phoneSas = Sas.Display(PairingSignature.ComputeSas(host.PublicKey, p.Spki, nh, nc));
        var confirm = Req(p, m);
        confirm.Stage = PairingStage.SasConfirm;
        var canonical = m == PairingMethod.RemoteCode ? RemoteCode.Normalize(code).Canonical ?? code : "";
        confirm.PairingToken = m == PairingMethod.RemoteCode ? code : "";
        confirm.Signature = ByteString.CopyFrom(SignV2(p, m, canonical, signedDeskId, nh, nc));
        Send(tls, confirm);
        var fin = RecvFinal(tls);
        await Done(server);
        return (fin, phoneSas);
    }
}

// M5 (spec 2.8): first message carries n_c, the host answers ATTENDED_CHALLENGE with a fresh n_h, the phone signs attended_v3.
PairingRequest AttendedFirst(Phone p, byte[]? nc)
{
    var r = Req(p, PairingMethod.TrustedAttended);
    if (nc is not null) r.AttendedClientNonce = ByteString.CopyFrom(nc);
    return r;
}
byte[] AttendedSig(Phone signer, Phone claimed, string hd, string rd, byte[] nh, byte[] nc) =>
    signer.Key.SignData(PairingSignature.BuildAttendedV3(host.PublicKey, claimed.Spki, claimed.Id, hd, rd, nh, nc),
        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
PairingRequest AttendedProof(Phone p, string hd, string rd, byte[] nh, byte[] nc)
{
    var r = Req(p, PairingMethod.TrustedAttended);
    r.Stage = PairingStage.AttendedProof;
    r.Signature = ByteString.CopyFrom(AttendedSig(p, p, hd, rd, nh, nc));
    return r;
}
(string Hd, string Rd) DeskIdsFor(PairingContext ctx) => ctx.Medium == ConnectionMedium.Remote ? (hostDeskId, peerDeskId) : ("", "");

/// <summary>Opens a connection, sends the first M5 message and reads the host's answer. Caller owns tls.</summary>
async Task<(SslStream Tls, Task Server, PairingResponse? Answer)> AttendedStart(Rig rig, Phone p, PairingContext ctx, byte[]? nc, Action<PairingRequest>? tweak = null)
{
    var (tls, server) = await Open(rig, ctx);
    var first = AttendedFirst(p, nc);
    tweak?.Invoke(first);
    Send(tls, first);
    return (tls, server, Recv(tls));
}

/// <summary>Full honest flow. Returns the final response; the challenge nonce is returned through <paramref name="issued"/>.</summary>
async Task<PairingResponse?> Attended(Rig rig, Phone p, PairingContext ctx, string? signedDeskId = null, List<byte[]>? issued = null)
{
    var nc = RandomNumberGenerator.GetBytes(32);
    var (tls, server, answer) = await AttendedStart(rig, p, ctx, nc);
    using (tls)
    {
        if (answer is null || answer.Stage != PairingStage.AttendedChallenge) { await Done(server); return answer; }
        var nh = answer.AttendedHostNonce.ToByteArray();
        issued?.Add(nh);
        var (hd, rd) = DeskIdsFor(ctx);
        if (signedDeskId is not null) { hd = signedDeskId; rd = signedDeskId.Length == 0 ? "" : peerDeskId; }
        Send(tls, AttendedProof(p, hd, rd, nh, nc));
        var fin = RecvFinal(tls);
        await Done(server);
        return fin;
    }
}

/// <summary>Honest first message, then the caller builds the proof from the issued n_h and its own n_c.</summary>
async Task<PairingResponse?> AttendedWith(Rig rig, Phone p, PairingContext ctx, Func<byte[], byte[], PairingRequest> buildProof)
{
    var nc = RandomNumberGenerator.GetBytes(32);
    var (tls, server, answer) = await AttendedStart(rig, p, ctx, nc);
    using (tls)
    {
        if (answer is null || answer.Stage != PairingStage.AttendedChallenge) { await Done(server); return answer; }
        Send(tls, buildProof(answer.AttendedHostNonce.ToByteArray(), nc));
        var fin = RecvFinal(tls);
        await Done(server);
        return fin;
    }
}

async Task<PairingResponse?> Unattended(Rig rig, Phone p, PairingContext ctx, string mode = "valid")
{
    var (tls, server) = await Open(rig, ctx);
    using (tls)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var r = Req(p, PairingMethod.TrustedUnattended);
        r.UnattendedReconnect = true;
        r.ReconnectClientNonce = ByteString.CopyFrom(nonce);
        Send(tls, r);
        var ch = Recv(tls);
        if (ch is null || ch.ReconnectChallenge.IsEmpty) { await Done(server); return ch; }
        var hostPayload = PairingSignature.BuildReconnectPayload(true, ch.ReconnectChallenge.ToByteArray(), nonce, host.DeviceId, p.Id,
            host.PublicKey, p.Spki, ch.ReconnectExpiresAtUnixMs);
        Check(DeviceIdentityService.Verify(host.PublicKey, hostPayload, ch.ReconnectHostSignature.ToByteArray()), "reconnect verifies signed host challenge " + mode);
        var clientPayload = PairingSignature.BuildReconnectPayload(false, ch.ReconnectChallenge.ToByteArray(), nonce, host.DeviceId, p.Id,
            host.PublicKey, p.Spki, ch.ReconnectExpiresAtUnixMs);
        var proof = Req(p, PairingMethod.TrustedUnattended);
        proof.UnattendedReconnect = true;
        proof.ReconnectClientNonce = ByteString.CopyFrom(nonce);
        proof.Signature = ByteString.CopyFrom(p.Key.SignData(mode == "reflection" ? hostPayload : clientPayload,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Send(tls, proof);
        var fin = RecvFinal(tls);
        await Done(server);
        return fin;
    }
}

// ---- downgrade: v1 / PIN is dead ----
{
    var rig = NewRig(NewOffer(), null);
    var (tls, server) = await Open(rig, PairingContext.Lan);
    using (tls)
    {
        var v1 = new PairingRequest
        {
            Requester = new DeviceIdentity { DeviceId = "phone", DisplayName = "Phone" },
            RequesterPublicKey = ByteString.CopyFrom(p0.Spki), PairingToken = "123456",
            Signature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(70)),
        };
        Send(tls, v1);
        var fin = RecvFinal(tls);
        await Done(server);
        Check(fin?.Result == PairingResult.Rejected && fin.Reason == PairingListener.UpdateReason, "v1 request (no method, PIN) rejected with update message");
    }
    Check(rig.Prompts.Count == 0 && !rig.Store.IsTrusted("phone"), "v1 request never prompts or trusts");
    var (t2, s2) = await Open(rig, PairingContext.Lan);
    using (t2)
    {
        var old = Req(p0, PairingMethod.Unspecified);
        old.UnattendedReconnect = true;
        old.ReconnectClientNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        Send(t2, old);
        var fin = RecvFinal(t2);
        await Done(s2);
        Check(fin?.Result == PairingResult.Rejected && fin.Reason == PairingListener.UpdateReason, "v1 unattended reconnect (no method) rejected as downgrade");
    }
}

// ---- M1 LAN QR ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId);
    var fin = await QrPair(rig, p0, PairingContext.Lan, PairingMethod.LanQr, offer.PairingToken, "");
    Check(fin?.Result == PairingResult.Accepted, "M1 valid QR pairing accepted after owner approval");
    Check(rig.Prompts.Count == 1 && rig.Prompts[0].Method == PairingMethod.LanQr && rig.Prompts[0].Medium == ConnectionMedium.Local &&
        rig.Prompts[0].PeerKeyId == KeyId.FromSpki(p0.Spki) && rig.Prompts[0].Sas is null, "M1 owner prompt carries method, medium and Key ID");
    Check(fin!.HostDeskId == hostDeskId, "final response carries host Desk ID");
    Check(rig.Store.IsTrusted("phone", p0.Spki) && !rig.Store.IsRemoteAllowed("phone", p0.Spki), "M1 trust stored with remote off by default");
    Check(rig.Sessions.Count == 1 && rig.Sessions[0].Method == PairingMethod.LanQr, "M1 hands an authenticated session to the receiver");
    var replay = await QrPair(rig, p0, PairingContext.Lan, PairingMethod.LanQr, offer.PairingToken, "");
    Check(replay?.Result == PairingResult.Rejected && rig.Prompts.Count == 1, "M1 consumed offer cannot be replayed and never re-prompts");

    var o2 = NewOffer();
    var rigB = NewRig(o2, hostDeskId, (_, _) => Task.FromResult(PairingApprovalResult.Declined));
    var declined = await QrPair(rigB, p0, PairingContext.Lan, PairingMethod.LanQr, o2.PairingToken, "");
    Check(declined?.Result == PairingResult.Rejected && !rigB.Store.IsTrusted("phone"), "M1 owner decline rejects and stores nothing");

    var o3 = NewOffer();
    var rigC = NewRig(o3, hostDeskId);
    var shortTok = await QrPair(rigC, p0, PairingContext.Lan, PairingMethod.LanQr, "123456", "");
    Check(shortTok?.Result == PairingResult.Rejected && rigC.Prompts.Count == 0, "6-digit PIN token rejected for QR method");
    var wrongTok = await QrPair(rigC, p0, PairingContext.Lan, PairingMethod.LanQr, QrToken.Generate(), "");
    Check(wrongTok?.Result == PairingResult.Rejected && rigC.Prompts.Count == 0, "wrong 22-char token rejected");
    var badSig = await QrPair(rigC, p0, PairingContext.Lan, PairingMethod.LanQr, o3.PairingToken, "", x => x.Signature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(70)));
    Check(badSig?.Result == PairingResult.Rejected && rigC.Prompts.Count == 0, "bad device signature rejected before any prompt");
    var other = new Phone("phone", "Phone");
    var rigD = NewRig(NewOffer(), hostDeskId);
    var asOther = await QrPair(rigD, p0, PairingContext.Lan, PairingMethod.LanQr, rigD.Offer!.PairingToken, "",
        x => x.Signature = ByteString.CopyFrom(SignV2(other, PairingMethod.LanQr, rigD.Offer!.PairingToken, "", null, null)));
    Check(asOther?.Result == PairingResult.Rejected && rigD.Prompts.Count == 0, "signature by a different key rejected");
    var expired = NewOffer(-1);
    var rigE = NewRig(expired, hostDeskId);
    var exp = await QrPair(rigE, p0, PairingContext.Lan, PairingMethod.LanQr, expired.PairingToken, "");
    Check(exp?.Result == PairingResult.ExpiredToken && rigE.Prompts.Count == 0, "expired offer rejected");
    var rigF = NewRig(null, hostDeskId);
    var none = await QrPair(rigF, p0, PairingContext.Lan, PairingMethod.LanQr, QrToken.Generate(), "");
    Check(none?.Result == PairingResult.Rejected, "QR with no current offer rejected");
    var rigG = NewRig(NewOffer(), hostDeskId);
    var remoteMethodOnLan = await QrPair(rigG, p0, PairingContext.Lan, PairingMethod.RemoteQr, rigG.Offer!.PairingToken, "");
    Check(remoteMethodOnLan?.Result == PairingResult.Rejected && rigG.Prompts.Count == 0, "REMOTE_QR over LAN rejected (method/medium table)");

    var rigH = NewRig(NewOffer(), hostDeskId);
    for (var i = 0; i < 5; i++) await QrPair(rigH, p0, PairingContext.Lan, PairingMethod.LanQr, QrToken.Generate(), "");
    var sixth = await QrPair(rigH, p0, PairingContext.Lan, PairingMethod.LanQr, rigH.Offer!.PairingToken, "");
    Check(sixth?.Result == PairingResult.Rejected && rigH.Prompts.Count == 0, "QR attempt budget exhausted, even the right token is refused");

    var rigI = NewRig(NewOffer(), hostDeskId);
    rigI.Store.AddOrUpdate("phone", "Phone", host.PublicKey);
    var swap = await QrPair(rigI, p0, PairingContext.Lan, PairingMethod.LanQr, rigI.Offer!.PairingToken, "");
    Check(swap?.Result == PairingResult.Rejected && rigI.Prompts.Count == 0 && rigI.Store.IsTrusted("phone", host.PublicKey) && !rigI.Store.IsTrusted("phone", p0.Spki),
        "re-pair with a different key under a known device ID rejected, stored key untouched");

    var rigJ = NewRig(NewOffer(), hostDeskId);
    rigJ.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    rigJ.Handler = (_, _) => { rigJ.Store.Remove("phone"); return Task.FromResult(new PairingApprovalResult(true)); };
    var rev = await QrPair(rigJ, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, rigJ.Offer!.PairingToken, hostDeskId);
    Check(rev?.Result == PairingResult.Rejected && !rigJ.Store.IsTrusted("phone"), "trust revoked during approval rejects the pairing");

    var rigK = NewRig(NewOffer(), hostDeskId);
    var firstOffer = rigK.Offer!;
    rigK.Handler = (_, _) => { rigK.Offer = NewOffer(); return Task.FromResult(new PairingApprovalResult(true)); };
    var rot = await QrPair(rigK, p0, PairingContext.Lan, PairingMethod.LanQr, firstOffer.PairingToken, "");
    Check(rot?.Result == PairingResult.Rejected && !rigK.Store.IsTrusted("phone"), "offer rotated during approval rejects the pairing");
}

// ---- M3 REMOTE QR ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId);
    var fin = await QrPair(rig, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, offer.PairingToken, hostDeskId);
    Check(fin?.Result == PairingResult.Accepted && rig.Prompts.Count == 1 && rig.Prompts[0].Medium == ConnectionMedium.Remote &&
        rig.Prompts[0].AllowRemoteDefault, "M3 remote QR needs the owner click and is remote by default");
    var rec = rig.Store.GetAll().Single();
    Check(rec.RemoteAllowed && rec.DeskId == peerDeskId && rec.PairedMethod == (int)PairingMethod.RemoteQr && !rec.UnattendedAllowed,
        "M3 stores Desk ID, remote flag and method; unattended stays off");

    var o2 = NewOffer();
    var rig2 = NewRig(o2, hostDeskId);
    var lanSig = await QrPair(rig2, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o2.PairingToken, "");
    Check(lanSig?.Result == PairingResult.Rejected && rig2.Prompts.Count == 0, "pipe pairing signed without the host Desk ID rejected");
    var lanMethod = await QrPair(NewRig(NewOffer(), hostDeskId), p0, Remote(p0, PipePurpose.PairQr), PairingMethod.LanQr, "x", hostDeskId);
    Check(lanMethod?.Result == PairingResult.Rejected, "LAN_QR over a pipe rejected");
    var o3 = NewOffer();
    var rig3 = NewRig(o3, null);
    var noDesk = await QrPair(rig3, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o3.PairingToken, "");
    Check(noDesk?.Result == PairingResult.Rejected && rig3.Prompts.Count == 0, "remote pairing refused when internet access is not set up");
    var o4 = NewOffer();
    var rig4 = NewRig(o4, hostDeskId);
    var liar = new PairingContext(ConnectionMedium.Remote, PipePurpose.PairQr, SHA256.HashData(host.PublicKey), peerDeskId);
    var mism = await QrPair(rig4, p0, liar, PairingMethod.RemoteQr, o4.PairingToken, hostDeskId);
    Check(mism?.Result == PairingResult.Rejected && rig4.Prompts.Count == 0, "server-reported requester fingerprint must match the presented key");
    var o5 = NewOffer();
    var rig5 = NewRig(o5, hostDeskId);
    var wrongPurpose = await QrPair(rig5, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteQr, o5.PairingToken, hostDeskId);
    Check(wrongPurpose?.Result == PairingResult.Rejected && rig5.Prompts.Count == 0, "pipe purpose must match the method");

    var o6 = NewOffer();
    var rig6 = NewRig(o6, hostDeskId);
    rig6.Store.AddOrUpdate("phone", "Phone", p0.Spki, new TrustEnrollment(peerDeskId, true, (int)PairingMethod.RemoteQr));
    var again = await QrPair(rig6, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o6.PairingToken, hostDeskId);
    Check(again?.Result == PairingResult.AlreadyTrusted && rig6.Prompts.Count == 0, "remote-allowed trusted key re-pairs without a prompt");
    var againSecond = await QrPair(rig6, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o6.PairingToken, hostDeskId);
    Check(againSecond?.Result == PairingResult.AlreadyTrusted, "already-trusted scan does not burn the QR token (second scan still works)");
    var newcomer = new Phone("phone2", "Phone 2");
    var stillFirstUse = await QrPair(rig6, newcomer, Remote(newcomer, PipePurpose.PairQr), PairingMethod.RemoteQr, o6.PairingToken, hostDeskId);
    Check(stillFirstUse?.Result == PairingResult.Accepted && rig6.Prompts.Count == 1, "token left unburnt still enrolls a new phone only with the owner click");
    var afterBurn = await QrPair(rig6, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o6.PairingToken, hostDeskId);
    Check(afterBurn?.Result == PairingResult.Rejected && afterBurn.Reason == PairingListener.QrAlreadyUsed,
        "a consumed QR answers with the clear 'already used, regenerate' reason");
    var o7 = NewOffer();
    var rig7 = NewRig(o7, hostDeskId);
    rig7.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    var lanOnly = await QrPair(rig7, p0, Remote(p0, PipePurpose.PairQr), PairingMethod.RemoteQr, o7.PairingToken, hostDeskId);
    Check(lanOnly?.Result == PairingResult.AlreadyTrusted && rig7.Prompts.Count == 1 && rig7.Prompts[0].AlreadyTrusted,
        "LAN-only trusted key over the pipe always prompts");
    Check(rig7.Store.IsRemoteAllowed("phone", p0.Spki), "approved remote pairing turns the remote flag on");
}

// ---- M2 LAN SAS ----
{
    var rig = NewRig(NewOffer(), hostDeskId);
    var (fin, phoneSas) = await SasPair(rig, p0, PairingContext.Lan, PairingMethod.LanSas, "", "");
    Check(fin?.Result == PairingResult.Accepted, "M2 SAS pairing accepted after owner approval");
    Check(rig.Prompts.Count == 1 && rig.Prompts[0].Sas == phoneSas && phoneSas!.Length == 7 && phoneSas[3] == ' ', "owner dialog shows the same SAS the phone computed");
    Check(rig.Store.IsTrusted("phone", p0.Spki) && !rig.Store.IsRemoteAllowed("phone", p0.Spki), "M2 trust stored with remote off");
    // 2026-11 fix: the PC shows the SAS while the phone compares (spec 2.2 step 4), not only after SAS_CONFIRM.
    Check(rig.Comparisons.Count == 1 && rig.Comparisons[0].Request.CompareOnly && rig.Comparisons[0].Request.Sas == phoneSas &&
        rig.Comparisons[0].BeforePrompt, "PC shows the SAS for comparison before the phone confirms and before the approval prompt");

    var noOffer = NewRig(null, hostDeskId);
    var closed = await SasPair(noOffer, p0, PairingContext.Lan, PairingMethod.LanSas, "", "");
    Check(closed.Final?.Result == PairingResult.Rejected && noOffer.Prompts.Count == 0, "M2 refused while no offer is current");

    var rigB = NewRig(NewOffer(), hostDeskId);
    var bad = await SasPair(rigB, p0, PairingContext.Lan, PairingMethod.LanSas, "", "", "badreveal");
    Check(bad.Final?.Result == PairingResult.Rejected && rigB.Prompts.Count == 0, "reveal that does not match the commitment rejected");

    var rigC = NewRig(NewOffer(), hostDeskId, (_, _) => Task.FromResult(PairingApprovalResult.Declined));
    var dec = await SasPair(rigC, p0, PairingContext.Lan, PairingMethod.LanSas, "", "");
    Check(dec.Final?.Result == PairingResult.Rejected && !rigC.Store.IsTrusted("phone"), "owner declining the SAS rejects");

    var rigD = NewRig(NewOffer(), hostDeskId);
    var (t1, s1) = await Open(rigD, PairingContext.Lan);
    using (t1)
    {
        var r = Req(p0, PairingMethod.LanSas);
        r.SasCommitment = ByteString.CopyFrom(Sas.Commitment(Sas.RandomNonce()));
        r.SasClientNonce = ByteString.CopyFrom(Sas.RandomNonce());
        Send(t1, r);
        var f = RecvFinal(t1);
        await Done(s1);
        Check(f?.Result == PairingResult.Rejected && f.Stage != PairingStage.SasHostNonce, "client nonce sent before the host nonce is rejected");
    }
    var (t2, s2) = await Open(rigD, PairingContext.Lan);
    using (t2)
    {
        var r = Req(p0, PairingMethod.LanSas);
        r.Stage = PairingStage.SasReveal;
        r.SasClientNonce = ByteString.CopyFrom(Sas.RandomNonce());
        Send(t2, r);
        var f = RecvFinal(t2);
        await Done(s2);
        Check(f?.Result == PairingResult.Rejected && f.Stage != PairingStage.SasHostNonce, "reveal as the first message rejected (no host nonce handed out)");
    }
    var (t3, s3) = await Open(rigD, PairingContext.Lan);
    using (t3)
    {
        var r = Req(p0, PairingMethod.LanSas);
        r.SasCommitment = ByteString.CopyFrom(new byte[16]);
        Send(t3, r);
        var f = RecvFinal(t3);
        await Done(s3);
        Check(f?.Result == PairingResult.Rejected && f.Stage != PairingStage.SasHostNonce, "malformed commitment length rejected");
    }

    var rigE = NewRig(NewOffer(), hostDeskId);
    var (t4, s4) = await Open(rigE, PairingContext.Lan);
    using (t4)
    {
        var nc = Sas.RandomNonce();
        var first = Req(p0, PairingMethod.LanSas);
        first.SasCommitment = ByteString.CopyFrom(Sas.Commitment(nc));
        Send(t4, first);
        Recv(t4);
        var rv = Req(p0, PairingMethod.LanSas); rv.Stage = PairingStage.SasReveal; rv.SasClientNonce = ByteString.CopyFrom(nc);
        Send(t4, rv);
        var cf = Req(p0, PairingMethod.LanSas); cf.Stage = PairingStage.SasConfirm;
        cf.Signature = ByteString.CopyFrom(SignV2(p0, PairingMethod.LanSas, "", "", Sas.RandomNonce(), nc));
        Send(t4, cf);
        var f = RecvFinal(t4);
        await Done(s4);
        Check(f?.Result == PairingResult.Rejected && rigE.Prompts.Count == 0, "SAS signature over a different transcript rejected");
    }

    var rigF = NewRig(NewOffer(), hostDeskId);
    rigF.Listener.ArmRemoteCode("482913", DateTimeOffset.UtcNow.AddMinutes(5));
    Check(rigF.Listener.ShouldAcceptPipe(PipePurpose.PairCode, null), "armed remote code is accepted on the pipe pre-check");
    for (var i = 0; i < 3; i++)
    {
        var f = await SasPair(rigF, p0, PairingContext.Lan, PairingMethod.LanSas, "", "", i == 2 ? "walkaway" : "badreveal");
        Check(f.Final is null || f.Final.Result == PairingResult.Rejected, "SAS failure " + (i + 1) + " rejected");
    }
    var paused = await SasPair(rigF, p0, PairingContext.Lan, PairingMethod.LanSas, "", "");
    Check(paused.Final?.Result == PairingResult.Rejected && rigF.Prompts.Count == 0 && (paused.Final?.Reason ?? "").Contains("paused"), "fourth SAS attempt refused during the cooldown");
    Check(!rigF.Listener.ShouldAcceptPipe(PipePurpose.PairCode, null), "pipe pre-check refuses pairing codes during the SAS cooldown");
    var pausedRemote = await SasPair(rigF, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(pausedRemote.Final?.Result == PairingResult.Rejected && rigF.Prompts.Count == 0, "cooldown also blocks the remote code method");

    var g = new PairingGate();
    g.RecordSasFailure(0); g.RecordSasFailure(1000);
    Check(!g.IsSasPaused(1001), "two SAS failures do not pause");
    g.RecordSasFailure(2000);
    Check(g.IsSasPaused(3000) && g.IsSasPaused(2000 + PairingGate.SasPauseMs - 1) && !g.IsSasPaused(2000 + PairingGate.SasPauseMs), "third failure pauses for exactly five minutes");
    var g2 = new PairingGate();
    g2.RecordSasFailure(0); g2.RecordSasFailure(1000); g2.RecordSasFailure(PairingGate.SasPauseMs + 5000);
    Check(!g2.IsSasPaused(PairingGate.SasPauseMs + 6000), "failures older than five minutes do not count");
}

// ---- M4 REMOTE CODE ----
{
    var rig = NewRig(null, hostDeskId);
    rig.Listener.ArmRemoteCode("482 913", DateTimeOffset.UtcNow.AddMinutes(5));
    var (fin, phoneSas) = await SasPair(rig, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(fin?.Result == PairingResult.Accepted && rig.Prompts.Count == 1 && rig.Prompts[0].Method == PairingMethod.RemoteCode &&
        rig.Prompts[0].Sas == phoneSas && rig.Prompts[0].Medium == ConnectionMedium.Remote, "M4 correct code plus SAS reaches the owner prompt");
    Check(rig.Comparisons.Count == 1 && rig.Comparisons[0].Request.Sas == phoneSas && rig.Comparisons[0].BeforePrompt &&
        rig.Comparisons[0].Request.Medium == ConnectionMedium.Remote, "M4 shows the SAS on the PC during the comparison");
    Check(rig.Store.IsRemoteAllowed("phone", p0.Spki) && rig.Store.GetAll().Single().DeskId == peerDeskId, "M4 enrollment is remote-allowed with the peer Desk ID");
    var p1 = new Phone("phone2", "Phone 2");
    var reuse = await SasPair(rig, p1, Remote(p1, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(reuse.Final?.Result == PairingResult.Rejected && rig.Prompts.Count == 1, "M4 code is single use");
    Check(!rig.Listener.ShouldAcceptPipe(PipePurpose.PairCode, null), "used code no longer accepted on the pipe pre-check");

    var rigB = NewRig(null, hostDeskId);
    rigB.Listener.ArmRemoteCode("482913", DateTimeOffset.UtcNow.AddMinutes(5));
    for (var i = 0; i < 5; i++)
    {
        var w = await SasPair(rigB, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "111111", hostDeskId);
        Check(w.Final?.Result == PairingResult.Rejected, "wrong remote code attempt " + (i + 1) + " rejected");
    }
    var late = await SasPair(rigB, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(late.Final?.Result == PairingResult.Rejected && rigB.Prompts.Count == 0, "after five wrong codes even the right code is refused (slot destroyed)");
    Check(!rigB.Listener.ShouldAcceptPipe(PipePurpose.PairCode, null), "destroyed slot no longer accepted on the pipe pre-check");

    var rigC = NewRig(null, hostDeskId);
    var unarmed = await SasPair(rigC, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(unarmed.Final?.Result == PairingResult.Rejected && rigC.Prompts.Count == 0, "M4 refused when no code slot is armed");
    rigC.Listener.ArmRemoteCode("482913", DateTimeOffset.UtcNow.AddMilliseconds(-1));
    var expiredSlot = await SasPair(rigC, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", hostDeskId);
    Check(expiredSlot.Final?.Result == PairingResult.Rejected, "expired code slot refused");
    try { rigC.Listener.ArmRemoteCode("12345", DateTimeOffset.UtcNow.AddMinutes(5)); throw new Exception("short code armed"); }
    catch (ArgumentException) { Check(true, "malformed code cannot be armed"); }

    var rigD = NewRig(null, hostDeskId);
    rigD.Listener.ArmRemoteCode("482913", DateTimeOffset.UtcNow.AddMinutes(5));
    var wrongDesk = await SasPair(rigD, p0, Remote(p0, PipePurpose.PairCode), PairingMethod.RemoteCode, "482913", "");
    Check(wrongDesk.Final?.Result == PairingResult.Rejected && rigD.Prompts.Count == 0, "M4 proof signed without the host Desk ID rejected");

    var slot = new RemoteCodeSlot("482913", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Check(slot.Check("482913", nowMs) == RemoteCodeCheck.Ok && slot.TryConsume(nowMs) && !slot.TryConsume(nowMs) && slot.Check("482913", nowMs) == RemoteCodeCheck.Dead, "slot is single use and wiped on use");
}

// ---- M5 trusted attended (fresh host nonce, spec 2.8) ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId);
    rig.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    var issued = new List<byte[]>();
    var fin = await Attended(rig, p0, PairingContext.Lan, null, issued);
    Check(fin?.Result == PairingResult.AlreadyTrusted && rig.Prompts.Count == 1 && rig.Prompts[0].Method == PairingMethod.TrustedAttended &&
        rig.Prompts[0].AlreadyTrusted && rig.Prompts[0].Timeout == TimeSpan.FromSeconds(30), "M5 trusted key still needs an owner Accept (30 s)");
    Check(fin!.AttendedHostNonce.IsEmpty && fin.Stage == PairingStage.Unspecified, "M5 success response carries no nonce and no challenge stage");
    await Attended(rig, p0, PairingContext.Lan, null, issued);
    Check(issued.Count == 2 && issued.All(n => n.Length == 32) && !issued[0].SequenceEqual(issued[1]), "M5 host nonce is 32 bytes and differs across connections");
    var viaQr = await QrPair(rig, new Phone("phone3", "P3"), PairingContext.Lan, PairingMethod.LanQr, offer.PairingToken, "");
    Check(viaQr?.Result == PairingResult.Accepted, "M5 reconnect did not consume the displayed QR offer");

    var rigB = NewRig(NewOffer(), hostDeskId);
    var untrusted = await Attended(rigB, p0, PairingContext.Lan);
    Check(untrusted?.Result == PairingResult.Rejected && untrusted.AttendedHostNonce.IsEmpty && rigB.Prompts.Count == 0,
        "M5 from an untrusted key rejected before any nonce, no prompt");

    var rigC = NewRig(NewOffer(), hostDeskId);
    rigC.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    var noRemote = await Attended(rigC, p0, Remote(p0, PipePurpose.Reconnect));
    Check(noRemote?.Result == PairingResult.Rejected && noRemote.AttendedHostNonce.IsEmpty && rigC.Prompts.Count == 0,
        "M5 over the pipe rejected before any nonce without remote_allowed");
    rigC.Store.SetRemoteAllowed("phone", p0.Spki, true);
    var withRemote = await Attended(rigC, p0, Remote(p0, PipePurpose.Reconnect));
    Check(withRemote?.Result == PairingResult.AlreadyTrusted && rigC.Prompts.Count == 1 && rigC.Prompts[0].Medium == ConnectionMedium.Remote,
        "M5 over the pipe accepted for a remote-allowed key after owner click");
    var lanSigOnPipe = await Attended(rigC, p0, Remote(p0, PipePurpose.Reconnect), "");
    Check(lanSigOnPipe?.Result == PairingResult.Rejected && rigC.Prompts.Count == 1, "M5 proof with empty Desk IDs rejected on the pipe, no prompt");
    {
        var nc = RandomNumberGenerator.GetBytes(32);
        var (tls, server, ans) = await AttendedStart(rigC, p0, PairingContext.Lan, nc);
        using (tls)
        {
            Check(ans?.Stage == PairingStage.AttendedChallenge, "M5 LAN challenge issued (pipe-proof-on-LAN setup)");
            Send(tls, AttendedProof(p0, hostDeskId, peerDeskId, ans!.AttendedHostNonce.ToByteArray(), nc));
            var f = RecvFinal(tls);
            await Done(server);
            Check(f?.Result == PairingResult.Rejected && rigC.Prompts.Count == 1, "M5 proof with Desk IDs on the LAN medium rejected, no prompt");
        }
    }
    foreach (var (label, ctxBad) in new[]
    {
        ("without requester Desk ID", Remote(p0, PipePurpose.Reconnect, "")),
        ("with a malformed requester Desk ID", Remote(p0, PipePurpose.Reconnect, "NOT-A-DESK-ID")),
    })
    {
        var (tls, server, ans) = await AttendedStart(rigC, p0, ctxBad, RandomNumberGenerator.GetBytes(32));
        using (tls) { await Done(server); }
        Check(ans?.Result == PairingResult.Rejected && ans.AttendedHostNonce.IsEmpty && ans.Stage == PairingStage.Unspecified && rigC.Prompts.Count == 1,
            "M5 pipe " + label + " rejected before a nonce is issued");
    }
    var wrongDeskIds = await AttendedWith(rigC, p0, Remote(p0, PipePurpose.Reconnect), (nh, nc) =>
        AttendedProof(p0, peerDeskId, hostDeskId, nh, nc));
    Check(wrongDeskIds?.Result == PairingResult.Rejected && rigC.Prompts.Count == 1, "M5 proof with host and requester Desk IDs swapped rejected, no prompt");
    var wrongHostDesk = await AttendedWith(rigC, p0, Remote(p0, PipePurpose.Reconnect), (nh, nc) =>
        AttendedProof(p0, DeskId.Generate(), peerDeskId, nh, nc));
    Check(wrongHostDesk?.Result == PairingResult.Rejected && rigC.Prompts.Count == 1, "M5 proof for a different host Desk ID rejected, no prompt");

    // Replay: everything a network or log observer saw on connection 1 is useless on connection 2.
    {
        var rigR = NewRig(NewOffer(), hostDeskId);
        rigR.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        var nc1 = RandomNumberGenerator.GetBytes(32);
        PairingRequest captured;
        {
            var (tls, server, ans) = await AttendedStart(rigR, p0, PairingContext.Lan, nc1);
            using (tls)
            {
                captured = AttendedProof(p0, "", "", ans!.AttendedHostNonce.ToByteArray(), nc1);
                Send(tls, captured);
                var f = RecvFinal(tls);
                await Done(server);
                Check(f?.Result == PairingResult.AlreadyTrusted && rigR.Prompts.Count == 1, "M5 genuine connection 1 reaches the prompt once");
            }
        }
        var (tls2, server2, ans2) = await AttendedStart(rigR, p0, PairingContext.Lan, nc1);
        using (tls2)
        {
            Check(ans2?.Stage == PairingStage.AttendedChallenge && ans2.AttendedHostNonce.Length == 32, "M5 replayed first message gets a brand new challenge");
            Send(tls2, captured);
            var f = RecvFinal(tls2);
            await Done(server2);
            Check(f?.Result == PairingResult.Rejected && f.Reason == "Device identity proof failed." && rigR.Prompts.Count == 1,
                "M5 replayed proof (old n_h) rejected on a new connection, zero new prompts");
        }
    }

    // Single use: a second proof on the same connection is never read, and a failed proof burns the nonce.
    {
        var rigS = NewRig(NewOffer(), hostDeskId);
        rigS.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        var nc = RandomNumberGenerator.GetBytes(32);
        var (tls, server, ans) = await AttendedStart(rigS, p0, PairingContext.Lan, nc);
        using (tls)
        {
            var nh = ans!.AttendedHostNonce.ToByteArray();
            var bad = AttendedProof(p0, "", "", nh, nc);
            bad.Signature = ByteString.CopyFrom(AttendedSig(p0, p0, "", "", nh, RandomNumberGenerator.GetBytes(32)));
            Send(tls, bad);
            var f = RecvFinal(tls);
            try { Send(tls, AttendedProof(p0, "", "", nh, nc)); } catch (IOException) { /* host already closed */ }
            await Done(server);
            Check(f?.Result == PairingResult.Rejected && rigS.Prompts.Count == 0, "M5 bad proof rejects; a valid second proof on the same connection is never read");
        }
    }

    // Wrong key, wrong device, wrong shape, wrong stage.
    {
        var other = new Phone("phone", "Phone");
        var rigK = NewRig(NewOffer(), hostDeskId);
        rigK.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        var wrongKey = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(p0, "", "", nh, nc);
            r.Signature = ByteString.CopyFrom(AttendedSig(other, p0, "", "", nh, nc));
            return r;
        });
        Check(wrongKey?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof signed by another key rejected, no prompt");
        var swappedKey = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(other, "", "", nh, nc);
            return r;
        });
        Check(swappedKey?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof presenting a different public key rejected, no prompt");
        var wrongDevice = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(p0, "", "", nh, nc);
            r.Requester.DeviceId = "someone-else";
            return r;
        });
        Check(wrongDevice?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof for a different device id rejected, no prompt");
        var wrongStage = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(p0, "", "", nh, nc);
            r.Stage = PairingStage.Unspecified;
            return r;
        });
        Check(wrongStage?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof with the wrong stage rejected, no prompt");
        var extra = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(p0, "", "", nh, nc);
            r.AttendedClientNonce = ByteString.CopyFrom(nc);
            return r;
        });
        Check(extra?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof that repeats the client nonce (extra field) rejected, no prompt");
        var legacyProof = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) =>
        {
            var r = AttendedProof(p0, "", "", nh, nc);
            r.Signature = ByteString.CopyFrom(SignV2(p0, PairingMethod.TrustedAttended, "", "", null, null));
            return r;
        });
        Check(legacyProof?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 pair/v2 method 5 signature no longer accepted as a proof");
        var swappedNonces = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) => AttendedProof(p0, "", "", nc, nh));
        Check(swappedNonces?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof with n_h and n_c swapped rejected, no prompt");
        var otherNc = await AttendedWith(rigK, p0, PairingContext.Lan, (nh, nc) => AttendedProof(p0, "", "", nh, RandomNumberGenerator.GetBytes(32)));
        Check(otherNc?.Result == PairingResult.Rejected && rigK.Prompts.Count == 0, "M5 proof over a different client nonce rejected, no prompt");
    }

    // Old one-shot format and bad first messages: never challenged, never prompted.
    {
        var rigO = NewRig(NewOffer(), hostDeskId);
        rigO.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        var (t1, s1, a1) = await AttendedStart(rigO, p0, PairingContext.Lan, null, r =>
            r.Signature = ByteString.CopyFrom(SignV2(p0, PairingMethod.TrustedAttended, "", "", null, null)));
        using (t1) { await Done(s1); }
        Check(a1?.Result == PairingResult.Rejected && a1.Reason == PairingListener.UpdateReason && a1.AttendedHostNonce.IsEmpty && rigO.Prompts.Count == 0,
            "M5 old one-shot signed request rejected with the update message, no prompt");
        foreach (var len in new[] { 0, 16, 31, 33, 64 })
        {
            var (t, s, a) = await AttendedStart(rigO, p0, PairingContext.Lan, new byte[len]);
            using (t) { await Done(s); }
            Check(a?.Result == PairingResult.Rejected && a.AttendedHostNonce.IsEmpty && a.Stage == PairingStage.Unspecified && rigO.Prompts.Count == 0,
                "M5 first message with a " + len + "-byte client nonce rejected before any challenge");
        }
        foreach (var (label, tweak) in new (string, Action<PairingRequest>)[]
        {
            ("with a token", r => r.PairingToken = "x"),
            ("with the unattended flag", r => r.UnattendedReconnect = true),
            ("with SAS fields", r => r.SasCommitment = ByteString.CopyFrom(new byte[32])),
            ("with a reconnect nonce", r => r.ReconnectClientNonce = ByteString.CopyFrom(new byte[32])),
            ("at the proof stage", r => r.Stage = PairingStage.AttendedProof),
        })
        {
            var (t, s, a) = await AttendedStart(rigO, p0, PairingContext.Lan, RandomNumberGenerator.GetBytes(32), tweak);
            using (t) { await Done(s); }
            Check(a?.Result == PairingResult.Rejected && a.AttendedHostNonce.IsEmpty && rigO.Prompts.Count == 0, "M5 first message " + label + " rejected, no challenge");
        }
    }

    // Expiry on the injected monotonic clock.
    {
        var rigT = NewRig(NewOffer(), hostDeskId);
        rigT.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        long clock = 1_000_000;
        rigT.Listener.MonotonicMs = () => Interlocked.Read(ref clock);
        var late = await AttendedWith(rigT, p0, PairingContext.Lan, (nh, nc) =>
        {
            Interlocked.Add(ref clock, 30_001);
            return AttendedProof(p0, "", "", nh, nc);
        });
        Check(late?.Result == PairingResult.Rejected && late.Reason.Contains("timed out") && rigT.Prompts.Count == 0,
            "M5 valid proof after 30 s rejected as timed out, no prompt");
        var justInTime = await AttendedWith(rigT, p0, PairingContext.Lan, (nh, nc) =>
        {
            Interlocked.Add(ref clock, 29_000);
            return AttendedProof(p0, "", "", nh, nc);
        });
        Check(justInTime?.Result == PairingResult.AlreadyTrusted && rigT.Prompts.Count == 1, "M5 valid proof inside the 30 s window accepted after owner click");
        var atDeadline = await AttendedWith(rigT, p0, PairingContext.Lan, (nh, nc) =>
        {
            Interlocked.Add(ref clock, 30_000);
            return AttendedProof(p0, "", "", nh, nc);
        });
        Check(atDeadline?.Result == PairingResult.Rejected && rigT.Prompts.Count == 1, "M5 proof exactly at the deadline rejected");
    }

    // L5: failed-attempt budget per peer key fingerprint (5 per 10 min), checked before any nonce or prompt.
    {
        var p1 = new Phone("phone-b", "Phone B");
        var rigL = NewRig(NewOffer(), hostDeskId);
        rigL.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        rigL.Store.AddOrUpdate("phone-b", "Phone B", p1.Spki);
        long clock = 5_000_000;
        rigL.Listener.MonotonicMs = () => Interlocked.Read(ref clock);
        async Task<PairingResponse?> BadFirst(Phone who)
        {
            var (t, sv, a) = await AttendedStart(rigL, who, PairingContext.Lan, new byte[31]);
            using (t) { await Done(sv); }
            return a;
        }
        for (var i = 0; i < 4; i++) await BadFirst(p0);
        Check((await Attended(rigL, p0, PairingContext.Lan))?.Result == PairingResult.AlreadyTrusted && rigL.Prompts.Count == 1,
            "M5 trusted reconnect still works after 4 failures");
        for (var i = 0; i < 5; i++) await BadFirst(p0);
        var blocked = await Attended(rigL, p0, PairingContext.Lan);
        Check(blocked?.Result == PairingResult.Rejected && blocked.Reason.Contains("Too many") && blocked.AttendedHostNonce.IsEmpty && rigL.Prompts.Count == 1,
            "M5 budget exhausted (success cleared the first four): rejected before any nonce or prompt");
        Check((await Attended(rigL, p1, PairingContext.Lan))?.Result == PairingResult.AlreadyTrusted && rigL.Prompts.Count == 2,
            "M5 budget is per key: another phone is unaffected");
        Interlocked.Add(ref clock, PairingGate.PeerWindowMs + 1);
        Check((await Attended(rigL, p0, PairingContext.Lan))?.Result == PairingResult.AlreadyTrusted && rigL.Prompts.Count == 3,
            "M5 budget expires after 10 minutes");

        var rigM = NewRig(NewOffer(), hostDeskId, (_, _) => Task.FromResult(PairingApprovalResult.Declined));
        rigM.Store.AddOrUpdate("phone", "Phone", p0.Spki);
        for (var i = 0; i < 5; i++) await Attended(rigM, p0, PairingContext.Lan);
        var spam = await Attended(rigM, p0, PairingContext.Lan);
        Check(rigM.Prompts.Count == 5 && spam?.Result == PairingResult.Rejected && spam.Reason.Contains("Too many"),
            "M5 repeated declined prompts stop after 5: no further Accept dialog");
    }

    var rigD = NewRig(NewOffer(), hostDeskId, (_, _) => Task.FromResult(PairingApprovalResult.Declined));
    rigD.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    Check((await Attended(rigD, p0, PairingContext.Lan))?.Result == PairingResult.Rejected, "M5 owner decline rejects");

    var rigE = NewRig(NewOffer(), hostDeskId);
    rigE.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    rigE.Handler = (_, _) => { rigE.Store.Remove("phone"); return Task.FromResult(new PairingApprovalResult(true)); };
    Check((await Attended(rigE, p0, PairingContext.Lan))?.Result == PairingResult.Rejected, "M5 revoked during the prompt rejects");

    var rigF = NewRig(NewOffer(), hostDeskId);
    rigF.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    rigF.Store.SetRemoteAllowed("phone", p0.Spki, true);
    rigF.Handler = (_, _) => { rigF.Store.SetRemoteAllowed("phone", p0.Spki, false); return Task.FromResult(new PairingApprovalResult(true)); };
    Check((await Attended(rigF, p0, Remote(p0, PipePurpose.Reconnect)))?.Result == PairingResult.Rejected, "remote flag turned off during the prompt rejects");
}

// ---- M6 trusted unattended ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId, (req, _) => req.Method == PairingMethod.TrustedUnattended ? throw new Exception("Unattended reconnect must never invoke the owner prompt") : Task.FromResult(new PairingApprovalResult(true)));
    rig.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    Check((await Unattended(rig, p0, PairingContext.Lan))?.Result == PairingResult.Rejected, "trusted key alone cannot bypass the unattended opt-in");
    rig.Store.SetUnattendedAllowed("phone", p0.Spki, true);
    Check((await Unattended(rig, p0, PairingContext.Lan, "reflection"))?.Result == PairingResult.Rejected, "wrong proof domain rejected over real TLS");
    Check((await Unattended(rig, p0, PairingContext.Lan))?.Result == PairingResult.AlreadyTrusted && rig.Sessions.Count == 1 && rig.Sessions[0].WasUnattended &&
        rig.Sessions[0].Method == PairingMethod.TrustedUnattended, "opted-in client reconnects with mutual proof");
    Check((await Unattended(rig, p0, PairingContext.Lan))?.Result == PairingResult.AlreadyTrusted && rig.Sessions.Count == 2,
        "a second reconnect works: budget and expiry are per connection, not tied to the QR offer");
    var viaQr = await QrPair(rig, new Phone("phone4", "P4"), PairingContext.Lan, PairingMethod.LanQr, offer.PairingToken, "");
    Check(viaQr?.Result == PairingResult.Accepted, "M6 reconnects did not consume the displayed QR offer");
    Check(rig.Sessions[0].PeerPublicKey.SequenceEqual(p0.Spki), "session carries the exact authenticated key");

    var noRemote = await Unattended(rig, p0, Remote(p0, PipePurpose.Reconnect));
    Check(noRemote?.Result == PairingResult.Rejected, "M6 over the pipe rejected without remote_allowed");
    rig.Store.SetRemoteAllowed("phone", p0.Spki, true);
    Check((await Unattended(rig, p0, Remote(p0, PipePurpose.Reconnect)))?.Result == PairingResult.AlreadyTrusted, "M6 over the pipe accepted for opted-in remote-allowed key");
    rig.Store.SetUnattendedAllowed("phone", p0.Spki, false);
    Check((await Unattended(rig, p0, PairingContext.Lan))?.Result == PairingResult.Rejected, "unattended opt-out takes effect immediately");
    rig.Store.SetUnattendedAllowed("phone", p0.Spki, true);
    rig.Store.Remove("phone");
    Check((await Unattended(rig, p0, PairingContext.Lan))?.Result == PairingResult.Rejected, "forgotten device cannot reconnect");
    rig.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    Check(!rig.Store.IsUnattendedAllowed("phone", p0.Spki), "forget and re-pair resets unattended opt-in");
}

// ---- pipe pre-check and entry points ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId);
    rig.Store.AddOrUpdate("phone", "Phone", p0.Spki);
    var fp = SHA256.HashData(p0.Spki);
    Check(!rig.Listener.ShouldAcceptPipe(PipePurpose.Reconnect, fp), "pre-check refuses a trusted key without remote_allowed");
    rig.Store.SetRemoteAllowed("phone", p0.Spki, true);
    Check(rig.Listener.ShouldAcceptPipe(PipePurpose.Reconnect, fp), "pre-check accepts a remote-allowed trusted key");
    Check(!rig.Listener.ShouldAcceptPipe(PipePurpose.Reconnect, SHA256.HashData(host.PublicKey)) && !rig.Listener.ShouldAcceptPipe(PipePurpose.Reconnect, null),
        "pre-check refuses unknown or missing fingerprints");
    Check(rig.Listener.ShouldAcceptPipe(PipePurpose.PairQr, null), "pre-check accepts QR pipes while an offer is current");
    rig.Offer = null;
    Check(!rig.Listener.ShouldAcceptPipe(PipePurpose.PairQr, null) && !rig.Listener.ShouldAcceptPipe(PipePurpose.PairCode, null) &&
        !rig.Listener.ShouldAcceptPipe(PipePurpose.Unspecified, fp), "pre-check refuses QR with no offer, unarmed code and unknown purposes");

    var tcp = new TcpListener(IPAddress.Loopback, 0);
    tcp.Start();
    using var c = new TcpClient();
    var cn = c.ConnectAsync((IPEndPoint)tcp.LocalEndpoint);
    using var acc = await tcp.AcceptTcpClientAsync();
    await cn; tcp.Stop();
    await rig.Listener.HandlePipeAsync(acc.GetStream(), PairingContext.Lan);
    var buf = new byte[1];
    var pipeClosed = false;
    try { pipeClosed = await c.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0; } catch (IOException) { pipeClosed = true; }
    Check(pipeClosed, "HandlePipeAsync closes a pipe given a Local context");
}

// ---- real LAN accept loop, end to end ----
{
    var offer = NewOffer();
    var rig = NewRig(offer, hostDeskId);
    var socketListener = new TcpListener(IPAddress.Loopback, 0);
    typeof(PairingListener).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Listener, socketListener);
    rig.Listener.Start();
    using var client = new TcpClient();
    await client.ConnectAsync((IPEndPoint)socketListener.LocalEndpoint);
    using var tls = new SslStream(client.GetStream(), false, (_, peer, _, _) =>
    {
        using var parsed = new X509Certificate2(peer!);
        return CryptographicOperations.FixedTimeEquals(host.PublicKey, parsed.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
    });
    await tls.AuthenticateAsClientAsync("ReyDesk").WaitAsync(TimeSpan.FromSeconds(5));
    tls.ReadTimeout = 10_000;
    var r = Req(p0, PairingMethod.LanQr);
    r.PairingToken = offer.PairingToken;
    r.Signature = ByteString.CopyFrom(SignV2(p0, PairingMethod.LanQr, offer.PairingToken, "", null, null));
    Send(tls, r);
    Check(RecvFinal(tls)?.Result == PairingResult.Accepted && rig.Store.IsTrusted("phone", p0.Spki), "real TCP accept loop completes an M1 pairing");
}

foreach (var open in liveSessions) { try { open.Owner.Dispose(); } catch (Exception) { /* closed */ } }
foreach (var path in cleanup) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* temp file */ } }
Console.WriteLine($"PASS {checks} pairing security checks passed");

internal sealed class Phone
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Spki { get; }
    public string Id { get; }
    public string Name { get; }
    public Phone(string id, string name) { Id = id; Name = name; Spki = Key.ExportSubjectPublicKeyInfo(); }
}

internal sealed class Rig
{
    public PairingListener Listener = null!;
    public TrustedDeviceStore Store = null!;
    public PairingOffer? Offer;
    public Func<PairingApprovalRequest, CancellationToken, Task<PairingApprovalResult>> Handler = null!;
    public List<PairingApprovalRequest> Prompts { get; } = new();
    public List<PairedSession> Sessions { get; } = new();
    /// <summary>Compare-only SAS displays, in order, plus whether each happened before any approval prompt.</summary>
    public List<(PairingApprovalRequest Request, bool BeforePrompt)> Comparisons { get; } = new();
}

internal sealed class StubGate(Rig rig) : IPairingApprovalGate
{
    public Task<PairingApprovalResult> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken ct)
    {
        lock (rig.Prompts) rig.Prompts.Add(request);
        return rig.Handler(request, ct);
    }

    public async Task<bool> ShowComparisonAsync(PairingApprovalRequest request, CancellationToken ct)
    {
        lock (rig.Prompts) rig.Comparisons.Add((request, rig.Prompts.Count == 0));
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return false; // the owner never cancels in this rig
    }
}

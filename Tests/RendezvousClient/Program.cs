using System.Security.Cryptography;
using System.Text.Json;
using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Rendezvous;

// Offline tests (URL normalisation, reconnect policy, ICE relay gating, scripted server) always run.
// The pipe echo against a local rendezvous service is skipped (exit 0) unless RENDEZVOUS_URL is set.
//   RENDEZVOUS_URL         e.g. ws://127.0.0.1:13260/v1/ws
//   RENDEZVOUS_SIGNED_URL  the service's PUBLIC_WS_URL if it differs (default = RENDEZVOUS_URL)
var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
    if (!ok) failures++;
}

try { await OfflineTests.RunAsync(Check); }
catch (Exception ex)
{
    Console.WriteLine("FAIL offline tests " + ex.GetType().Name + ": " + ex.Message);
    failures++;
}

var url = Environment.GetEnvironmentVariable("RENDEZVOUS_URL");
if (string.IsNullOrWhiteSpace(url))
{
    Console.WriteLine(failures == 0
        ? "PASS RendezvousClient (offline tests; online part skipped: RENDEZVOUS_URL not set)"
        : $"FAILED RendezvousClient ({failures})");
    return failures == 0 ? 0 : 1;
}
var signed = Environment.GetEnvironmentVariable("RENDEZVOUS_SIGNED_URL");

var dir = Path.Combine(Path.GetTempPath(), "reydesk-rendezvous-test");
Directory.CreateDirectory(dir);
var hostId = TestIdentity.Load(Path.Combine(dir, "host.json"));
var phoneId = TestIdentity.Load(Path.Combine(dir, "phone.json"));

await using var host = new RendezvousClient(url, hostId, signed, "windows-test", "0");
await using var phone = new RendezvousClient(url, phoneId, signed, "android-test", "0");
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct = overall.Token;

try
{
    host.Start();
    phone.Start();
    await host.WaitOnlineAsync(TimeSpan.FromSeconds(15), ct);
    await phone.WaitOnlineAsync(TimeSpan.FromSeconds(15), ct);

    Check("host registered with valid Desk ID", DeskId.Parse(host.DeskId).Ok);
    Check("phone registered with valid Desk ID", DeskId.Parse(phone.DeskId).Ok);
    Check("distinct Desk IDs", host.DeskId != phone.DeskId);
    Check("Desk ID persisted in identity store", hostId.DeskId == host.DeskId);

    var ice = await host.IceServers.GetAsync(ct);
    Check("IceConfig received", ice.Servers.Count > 0 && ice.Servers.All(s => s.Urls.Count > 0));

    // Host: accept every pipe and echo until EOF.
    var requests = new System.Collections.Concurrent.ConcurrentBag<(PipePurpose, string)>();
    host.PipeRequested += req => _ = Task.Run(async () =>
    {
        requests.Add((req.Purpose, req.RequesterDeskId));
        var pipe = await req.AcceptAsync(CancellationToken.None);
        if (pipe is null) return;
        await using (pipe)
        {
            var buf = new byte[RelayPipeStream.MaxChunk];
            int n;
            while ((n = await pipe.ReadAsync(buf, CancellationToken.None)) > 0)
                await pipe.WriteAsync(buf.AsMemory(0, n), CancellationToken.None);
        }
    });

    // Echo: small, then 96 KiB (multi-frame, hits the rate/backpressure path).
    await using (var pipe = await phone.OpenPipeAsync(host.DeskId!, PipePurpose.Reconnect, null, ct))
    {
        Check("peer SPKI is the host key", pipe.PeerSpki.AsSpan().SequenceEqual(hostId.PublicKey));
        foreach (var size in new[] { 5, 96 * 1024 })
        {
            var payload = RandomNumberGenerator.GetBytes(size);
            var writer = pipe.WriteAsync(payload, ct).AsTask();
            var echoed = new byte[size];
            var got = 0;
            while (got < size)
            {
                var n = await pipe.ReadAsync(echoed.AsMemory(got), ct);
                if (n == 0) break;
                got += n;
            }
            await writer;
            Check($"echo {size} bytes", got == size && echoed.AsSpan().SequenceEqual(payload));
        }
    }
    Check("host saw requester as phone, purpose RECONNECT",
        requests.Any(r => r.Item1 == PipePurpose.Reconnect && r.Item2 == phone.DeskId));

    // Unknown target -> UNAVAILABLE.
    var ghost = DeskId.FromBits(0x123456789ABCDEUL & ((1UL << 55) - 1));
    try
    {
        await phone.OpenPipeAsync(ghost, PipePurpose.Reconnect, null, ct);
        Check("unknown target fails", false);
    }
    catch (RendezvousPipeException ex)
    {
        Check("unknown target -> UNAVAILABLE", ex.Reason == PipeFailReason.Unavailable);
    }

    // Malformed Desk ID never reaches the network.
    try { await phone.OpenPipeAsync("AAAA-AAAA-AAAA", PipePurpose.Reconnect, null, ct); Check("bad checksum rejected locally", false); }
    catch (ArgumentException) { Check("bad checksum rejected locally", true); }

    // Slot create + status.
    var status = new TaskCompletionSource<PairSlotStatusInfo>();
    host.PairSlotStatusChanged += s => status.TrySetResult(s);
    await host.CreatePairSlotAsync("123456", 60, ct);
    var st = await status.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    Check("pair slot created (5 attempts left)", st.AttemptsLeft == 5 && !st.Expired);
    await host.CancelPairSlotAsync(ct);

    // Same key reconnects to the same Desk ID after a restart.
    var firstHostId = host.DeskId;
    await host.StopAsync();
    await using var host2 = new RendezvousClient(url, hostId, signed, "windows-test", "0");
    host2.Start();
    await host2.WaitOnlineAsync(TimeSpan.FromSeconds(15), ct);
    Check("restart keeps Desk ID", host2.DeskId == firstHostId);
    await host2.StopAsync();
}
catch (Exception ex)
{
    // Type and message only; never frame contents.
    Console.WriteLine("FAIL unexpected " + ex.GetType().Name + ": " + ex.Message);
    failures++;
}

Console.WriteLine(failures == 0 ? "PASS RendezvousClient" : $"FAILED RendezvousClient ({failures})");
return failures == 0 ? 0 : 1;

/// <summary>Throw-away P-256 identity for tests, kept in the temp folder so repeated runs do not exhaust
/// the service's registration rate limit. Holds a test-only key.</summary>
sealed class TestIdentity : IRendezvousIdentity
{
    private readonly string _path;
    private readonly ECDsa _key;

    private TestIdentity(string path, ECDsa key, string? deskId) { _path = path; _key = key; DeskId = deskId; }

    public static TestIdentity Load(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string? deskId = null;
        if (File.Exists(path))
        {
            var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(path))!;
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(rec.Key), out _);
            deskId = rec.DeskId;
        }
        var id = new TestIdentity(path, key, deskId);
        id.Save();
        return id;
    }

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();
    public string? DeskId { get; private set; }
    public byte[] Sign(byte[] data) => IdentitySignature.Sign(_key, data);
    public void SetDeskId(string? deskId) { DeskId = deskId; Save(); }

    private void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(new Record(Convert.ToBase64String(_key.ExportPkcs8PrivateKey()), DeskId)));

    private sealed record Record(string Key, string? DeskId);
}

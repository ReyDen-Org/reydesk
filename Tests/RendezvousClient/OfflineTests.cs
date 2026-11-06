using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Google.Protobuf;
using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Rendezvous;

/// <summary>Tests that need no rendezvous service: URL normalisation, reconnect policy, ICE relay gating and the client
/// loop against a scripted in-process WebSocket server (T18 H1 steps 3-4, H2 client side).</summary>
internal static class OfflineTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        Urls(check);
        Policy(check);
        await IceProviderAsync(check);
        await ScriptedServerAsync(check);
    }

    private static void Urls(Action<string, bool> check)
    {
        string N(string? s, bool dev = false) => RendezvousUrl.TryNormalize(s, dev, out var u) ? u : "<invalid>";
        check("url: built-in default is canonical", N("wss://rdv.reyden.ir/v1/ws") == "wss://rdv.reyden.ir/v1/ws" && N("WSS://Rdv.Reyden.IR:443/v1/ws/") == "wss://rdv.reyden.ir/v1/ws");
        check("url: lowercase scheme and host", N("WSS://Rendezvous.Example.COM/v1/ws") == "wss://rendezvous.example.com/v1/ws");
        check("url: trim and trailing slash", N("  wss://host.example/v1/ws/  ") == "wss://host.example/v1/ws");
        check("url: bare host slash", N("wss://host.example/") == "wss://host.example");
        check("url: default port dropped", N("wss://host.example:443/v1/ws") == "wss://host.example/v1/ws");
        check("url: other port kept", N("wss://host.example:8443/v1/ws") == "wss://host.example:8443/v1/ws");
        check("url: variants collapse to one", N("wss://HOST.example:443/v1/ws/") == N("wss://host.example/v1/ws"));
        check("url: path case kept", N("wss://host.example/V1/Ws") == "wss://host.example/V1/Ws");
        check("url: ws rejected for public host even in dev", N("ws://host.example/v1/ws", dev: true) == "<invalid>");
        check("url: ws rejected when dev off", N("ws://127.0.0.1:13260/v1/ws") == "<invalid>");
        foreach (var host in new[] { "127.0.0.1:13260", "localhost:1", "10.0.2.2:13260", "pc.local" })
            check("url: dev ws accepted for " + host, N($"WS://{host}/v1/ws/", dev: true) == $"ws://{host}/v1/ws");
        check("url: http rejected", N("https://host.example/v1/ws", dev: true) == "<invalid>");
        check("url: no scheme rejected", N("host.example/v1/ws") == "<invalid>");
        check("url: empty rejected", N("  ") == "<invalid>" && N(null) == "<invalid>");
        check("url: credentials rejected", N("wss://user:pw@host.example/v1/ws") == "<invalid>");
        check("url: query and fragment rejected", N("wss://host.example/v1/ws?x=1") == "<invalid>" && N("wss://host.example/v1/ws#a") == "<invalid>");
        check("url: inner whitespace rejected", N("wss://host .example/v1/ws") == "<invalid>");
        var ctorThrows = false;
        try { _ = new RendezvousClient("http://host.example/", new FakeIdentity(), allowCleartextForDev: true); }
        catch (ArgumentException) { ctorThrows = true; }
        check("url: client constructor rejects a bad URL", ctorThrows);
    }

    private static void Policy(Action<string, bool> check)
    {
        check("policy: BAD_SIGNATURE stops", ReconnectPolicy.Decide(AuthError.BadSignature) == ReconnectAction.Stop);
        check("policy: KEY_MISMATCH stops", ReconnectPolicy.Decide(AuthError.KeyMismatch) == ReconnectAction.Stop);
        check("policy: UNKNOWN_ID re-registers", ReconnectPolicy.Decide(AuthError.UnknownId) == ReconnectAction.Reregister);
        check("policy: RATE_LIMITED retries", ReconnectPolicy.Decide(AuthError.RateLimited) == ReconnectAction.Retry);
        check("policy: backoff grows 1,2,4", Seconds(0, null, 0) == 1 && Seconds(1, null, 0) == 2 && Seconds(2, null, 0) == 4);
        check("policy: backoff capped at 60 s even with jitter", Seconds(30, null, 0.2) == 60 && Seconds(6, null, 0.2) == 60 && Seconds(5, null, 0.2) <= 60);
        check("policy: jitter within 20%", Seconds(3, null, -0.2) == 6.4 && Seconds(3, null, 0.2) == 9.6);
        check("policy: retry-after is a floor", Seconds(0, TimeSpan.FromSeconds(20), 0) == 20);
        check("policy: backoff above retry-after wins", Seconds(4, TimeSpan.FromSeconds(2), 0) == 16);
        check("policy: absurd retry-after is capped", Seconds(0, TimeSpan.FromHours(5), 0) == 300);
        static double Seconds(int attempt, TimeSpan? ra, double jitter) => Math.Round(ReconnectPolicy.Delay(attempt, ra, jitter).TotalSeconds, 3);
    }

    private static async Task IceProviderAsync(Action<string, bool> check)
    {
        static IceConfig Config(bool withTurn)
        {
            var c = new IceConfig { ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds() };
            c.IceServers.Add(new IceServer { Urls = { "stun:stun.example:3478" } });
            if (withTurn) c.IceServers.Add(new IceServer { Urls = { "turn:turn.example:3478", "turns:turn.example:5349" }, Username = "u", Credential = "secret" });
            return c;
        }

        var requests = 0;
        var relay = false;
        var provider = new IceServerProvider(() => { requests++; return Task.CompletedTask; }, () => relay);
        provider.Update(Config(withTurn: true)); // an older server that still sends TURN in the first IceConfig
        var held = await provider.GetAsync();
        check("ice: relay off drops TURN servers and credentials",
            held.Servers.Count == 1 && held.Servers.All(s => s.Credential is null && s.Username is null && !s.Urls.Any(IceServerProvider.IsTurn)));
        check("ice: relay off never sends IceConfigRequest", requests == 0);
        held = await provider.GetAsync();
        check("ice: relay off stays quiet on repeat", requests == 0 && held.Servers.Count == 1);

        relay = true;
        var pending = provider.GetAsync();
        await Task.Delay(100);
        check("ice: relay on sends exactly one IceConfigRequest", requests == 1);
        provider.Update(Config(withTurn: true));
        var withTurn = await pending;
        check("ice: relay on keeps TURN credentials from the answer",
            withTurn.Servers.Any(s => s.Credential == "secret" && s.Urls.Any(IceServerProvider.IsTurn)));
        _ = await provider.GetAsync();
        check("ice: valid TURN is not requested again", requests == 1);

        // Relay on but the server has relay disabled: answer is STUN only; GetAsync falls back, no request storm.
        var requests2 = 0;
        IceServerProvider? p2 = null;
        p2 = new IceServerProvider(() => { requests2++; p2!.Update(Config(withTurn: false)); return Task.CompletedTask; }, () => true);
        p2.Update(Config(withTurn: false));
        var a = await p2.GetAsync();
        var b = await p2.GetAsync();
        check("ice: relay disabled on server falls back to STUN, throttled", requests2 == 1 && a.Servers.Count == 1 && b.Servers.Count == 1);
    }

    private static async Task ScriptedServerAsync(Action<string, bool> check)
    {
        // BAD_SIGNATURE and KEY_MISMATCH: one attempt, then Failed, no reconnect loop.
        foreach (var error in new[] { AuthError.BadSignature, AuthError.KeyMismatch })
        {
            await using var server = new ScriptedServer(_ => new RendezvousFrame { AuthResult = new AuthResult { Error = error } });
            await using var client = NewClient(server.Url);
            client.Start();
            var failed = await WaitStateAsync(client, RendezvousState.Failed, TimeSpan.FromSeconds(8));
            await Task.Delay(2500); // a retry would come after about 1 s
            check($"loop: {error} ends in Failed state", failed && client.State == RendezvousState.Failed && client.LastError == error.ToString());
            check($"loop: {error} is not retried", server.Connections == 1);
        }

        // RATE_LIMITED + Error(retry_after): the next attempt waits at least that long.
        {
            await using var server = new ScriptedServer(_ => new RendezvousFrame { AuthResult = new AuthResult { Error = AuthError.RateLimited } },
                follow: new RendezvousFrame { Error = new Error { Code = ErrorCode.RateLimited, RetryAfterMs = 2500 } });
            await using var client = NewClient(server.Url);
            client.Start();
            await server.WaitConnectionsAsync(2, TimeSpan.FromSeconds(12));
            double gap;
            lock (server.ConnectTimes) gap = server.ConnectTimes.Count >= 2 ? (server.ConnectTimes[1] - server.ConnectTimes[0]).TotalSeconds : 0;
            check("loop: RATE_LIMITED honours retry-after (>= 2.4 s)", gap >= 2.4 && gap < 8);
            check("loop: RATE_LIMITED is not fatal", client.State != RendezvousState.Failed);
        }

        // Relay off: auth ok, initial IceConfig, and no IceConfigRequest ever arrives, even if a session asks twice.
        {
            await using var server = new ScriptedServer(_ => new RendezvousFrame
            {
                AuthResult = new AuthResult { Ok = true, DeskId = DeskId.FromBits(0x123456) },
            }, afterAuth: IceFrame(withTurn: true));
            var relay = false;
            await using var client = NewClient(server.Url, () => relay);
            client.Start();
            await client.WaitOnlineAsync(TimeSpan.FromSeconds(8));
            var ice = await client.IceServers.GetAsync();
            await client.IceServers.GetAsync();
            await Task.Delay(300);
            check("wire: relay off sends no IceConfigRequest", server.IceRequests == 0);
            check("wire: relay off holds STUN only", ice.Servers.Count > 0 && ice.Servers.All(s => s.Credential is null && !s.Urls.Any(IceServerProvider.IsTurn)));

            relay = true;
            var withTurn = await client.IceServers.GetAsync();
            check("wire: relay on sends IceConfigRequest and uses the TURN answer",
                server.IceRequests == 1 && withTurn.Servers.Any(s => s.Credential == "secret"));
        }
    }

    internal static RendezvousFrame IceFrame(bool withTurn)
    {
        var c = new IceConfig { ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds() };
        c.IceServers.Add(new IceServer { Urls = { "stun:stun.example:3478" } });
        if (withTurn) c.IceServers.Add(new IceServer { Urls = { "turn:turn.example:3478" }, Username = "u", Credential = "secret" });
        return new RendezvousFrame { IceConfig = c };
    }

    private static RendezvousClient NewClient(string url, Func<bool>? relay = null) =>
        new(url, new FakeIdentity(), null, "windows-test", "0", relay, allowCleartextForDev: true);

    private static async Task<bool> WaitStateAsync(RendezvousClient client, RendezvousState state, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (client.State == state) return true;
            await Task.Delay(25);
        }
        return client.State == state;
    }
}

internal sealed class FakeIdentity : IRendezvousIdentity
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();
    public string? DeskId { get; private set; }
    public byte[] Sign(byte[] data) => IdentitySignature.Sign(_key, data);
    public void SetDeskId(string? deskId) => DeskId = deskId;
}

/// <summary>Minimal rendezvous stand-in on 127.0.0.1: sends ServerHello, answers ClientAuth with the scripted frame
/// (then an optional follow-up), and for authenticated connections answers IceConfigRequest with TURN credentials.</summary>
internal sealed class ScriptedServer : IAsyncDisposable
{
    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ClientAuth, RendezvousFrame> _auth;
    private readonly RendezvousFrame? _follow;
    private readonly RendezvousFrame? _afterAuth;
    private readonly Task _loop;
    private int _connections;
    private int _iceRequests;

    public ScriptedServer(Func<ClientAuth, RendezvousFrame> auth, RendezvousFrame? follow = null, RendezvousFrame? afterAuth = null)
    {
        _auth = auth;
        _follow = follow;
        _afterAuth = afterAuth;
        int port;
        while (true)
        {
            port = Random.Shared.Next(20000, 40000);
            _http.Prefixes.Clear();
            _http.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _http.Start(); break; } catch (HttpListenerException) { }
        }
        Url = $"ws://127.0.0.1:{port}/v1/ws";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Url { get; }
    public int Connections => Volatile.Read(ref _connections);
    public int IceRequests => Volatile.Read(ref _iceRequests);
    public List<DateTime> ConnectTimes { get; } = new();

    public async Task WaitConnectionsAsync(int count, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Connections < count && DateTime.UtcNow < until) await Task.Delay(25);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            lock (ConnectTimes) ConnectTimes.Add(DateTime.UtcNow);
            Interlocked.Increment(ref _connections);
            await SendAsync(ws, new RendezvousFrame { ServerHello = new ServerHello { Protocol = 1, Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) } });
            var first = await ReceiveAsync(ws);
            if (first is null || first.MsgCase != RendezvousFrame.MsgOneofCase.ClientAuth) return;
            var reply = _auth(first.ClientAuth);
            await SendAsync(ws, reply);
            if (reply.AuthResult is not { Ok: true })
            {
                if (_follow is not null) await SendAsync(ws, _follow);
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth_failed", CancellationToken.None);
                return;
            }
            if (_afterAuth is not null) await SendAsync(ws, _afterAuth);
            while (ws.State == WebSocketState.Open)
            {
                var f = await ReceiveAsync(ws);
                if (f is null) break;
                if (f.MsgCase == RendezvousFrame.MsgOneofCase.IceConfigRequest)
                {
                    Interlocked.Increment(ref _iceRequests);
                    await SendAsync(ws, OfflineTests.IceFrame(withTurn: true));
                }
                else if (f.MsgCase == RendezvousFrame.MsgOneofCase.Ping)
                    await SendAsync(ws, new RendezvousFrame { Pong = new Pong() });
            }
        }
        catch (Exception) { /* client went away; nothing to clean up */ }
    }

    private static Task SendAsync(WebSocket ws, RendezvousFrame f) =>
        ws.SendAsync(f.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<RendezvousFrame?> ReceiveAsync(WebSocket ws)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return RendezvousFrame.Parser.ParseFrom(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _http.Close(); } catch (Exception) { }
        try { await _loop; } catch (Exception) { }
    }
}

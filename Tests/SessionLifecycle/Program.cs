using System.Net;
using System.Net.Sockets;
using ReyDesk.Windows;
using ReyDesk.Windows.Signaling;
using Google.Protobuf;
using ReyDesk.Protocol.V1;

var count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    count++;
    Console.WriteLine("PASS: " + label);
}
var registry = new SessionRegistry<SessionTeardown>();
var oldSessionClosed = 0;
var oldConnectionClosed = 0;
SessionTeardown? old = null;
old = new SessionTeardown(() => oldSessionClosed++, () =>
{
    oldConnectionClosed++;
    registry.Remove("phone", old!); // Simulate synchronous stale Closed callback.
});
registry.Replace("phone", old);
var replacement = new SessionTeardown(() => { }, () => { });
registry.Replace("phone", replacement)?.Dispose();
Check(oldSessionClosed == 1 && oldConnectionClosed == 1, "re-pairing closes old media and connection exactly once");
Check(registry.IsCurrent("phone", replacement), "old Closed callback cannot remove replacement");
Check(!registry.Remove("phone", old), "delayed old close cannot clear current ownership");
Parallel.For(0, 30, _ => old.Dispose());
Check(oldSessionClosed == 1 && oldConnectionClosed == 1, "racing repeated teardown stays idempotent");
var drained = registry.Drain();
Check(drained.Length == 1 && registry.Get("phone") is null, "shutdown snapshots ownership before callbacks");
var connectionFinallyClosed = 0;
var throwing = new SessionTeardown(() => throw new InvalidOperationException("test"), () => connectionFinallyClosed++);
try { throwing.Dispose(); } catch (InvalidOperationException) { }
throwing.Dispose();
Check(connectionFinallyClosed == 1, "connection still closes if media teardown fails");

async Task<(TcpClient Peer, SignalingConnection Signaling)> Pair()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new TcpClient();
    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    var accepted = await listener.AcceptTcpClientAsync();
    listener.Stop();
    return (client, new SignalingConnection(accepted));
}
var pair = await Pair();
using (pair.Peer)
using (pair.Signaling)
{
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var closed = 0;
    pair.Signaling.Closed += () => { Interlocked.Increment(ref closed); ended.TrySetResult(); };
    pair.Signaling.StartReadLoop();
    pair.Peer.Client.Shutdown(SocketShutdown.Send);
    await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(closed == 1, "actual TCP clean EOF notifies Closed");
    pair.Signaling.Dispose();
    Check(closed == 1, "EOF then local dispose emits Closed only once");
}
pair = await Pair();
using (pair.Peer)
using (pair.Signaling)
{
    var closed = 0;
    pair.Signaling.Closed += () => Interlocked.Increment(ref closed);
    pair.Signaling.StartReadLoop();
    pair.Signaling.Dispose();
    Check(closed == 1, "local disconnect notifies immediately while read is blocked");
    Parallel.For(0, 30, _ => pair.Signaling.Dispose());
    Check(closed == 1, "racing socket cleanup never raises duplicate Closed");
}
var oldAnswer = SessionAnswer.Parser.ParseFrom(new SessionAnswer { SessionId = "legacy", Sdp = "answer" }.ToByteArray());
Check(oldAnswer.RequestedConfig is null, "old peer answer without requested config remains compatible");
var requestedAnswer = SessionAnswer.Parser.ParseFrom(new SessionAnswer
{
    SessionId = "new", Sdp = "answer",
    RequestedConfig = new NegotiatedConfig { HeightPx = 720, Fps = 30, TargetBitrateKbps = 4000 },
}.ToByteArray());
Check(requestedAnswer.RequestedConfig.HeightPx == 720 && requestedAnswer.RequestedConfig.Fps == 30 &&
    requestedAnswer.RequestedConfig.TargetBitrateKbps == 4000, "new peer profile survives protobuf signaling roundtrip");
// T18 M1: a live remote session ends when "Allow over internet" is turned off, globally or per device.
Check(SessionAccessPolicy.RemoteSessionPermitted(true, true, true), "remote session allowed while both switches are on");
Check(!SessionAccessPolicy.RemoteSessionPermitted(true, false, true), "global Allow over internet off ends a live remote session");
Check(!SessionAccessPolicy.RemoteSessionPermitted(true, true, false), "per-device remote permission off ends a live remote session");
Check(SessionAccessPolicy.RemoteSessionPermitted(false, false, false), "LAN session is not affected by the internet switches");
Console.WriteLine($"{count} lifecycle regression checks passed");

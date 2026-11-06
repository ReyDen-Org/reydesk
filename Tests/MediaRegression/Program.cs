using ReyDesk.Windows.Media;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
var timing = new VideoFrameTiming(15);
Check(timing.TryAccept(10_000_000, 10_000_000, out var first) && first == 0, "first fresh frame starts media clock at zero");
Check(!timing.TryAccept(10_100_000, 10_100_000, out _), "10ms capture burst is dropped before conversion");
Check(timing.TryAccept(11_000_000, 11_000_000, out var next) && next == 1_000_000, "100ms elapsed advances actual sample clock100ms");
Check(!timing.TryAccept(11_000_000, 11_000_000, out _), "duplicate captured frame is dropped");
Check(!timing.TryAccept(12_000_000, 20_000_000, out _), "800ms old queued frame is dropped");
Check(timing.TryAccept(20_000_000, 20_000_000, out var afterStall) && afterStall == 10_000_000, "one second stall stays one second on media clock");
Check(!timing.TryAccept(20_010_000, 20_010_000, out _), "no catch-up burst after stall");
Check(VideoFrameTiming.ToRtpTimestamp(1_000_000) == 9000, "100ms media time maps to9000 RTP ticks");
Check(VideoFrameTiming.ToRtpTimestamp(10_000_000) == 90000, "one second maps to90000 RTP ticks");
Check(VideoFrameTiming.ToRtpTimestamp(10_000_000L * 60 * 60 * 14) == 241032704u, "RTP clock wraps at32bits without crashing long session");
var displayRate = new VideoFrameTiming(15);
var accepted = 0;
for (var index = 0; index < 600; index++) {
    var source = 10_000_000L + index * 10_000_000L / 60;
    if (displayRate.TryAccept(source, source, out _)) accepted++;
}
Check(accepted >= 149 && accepted <= 151, "60Hz capture sustains15fps over10seconds despite timestamp rounding");
var invalidSource = new VideoFrameTiming(15);
Check(!invalidSource.TryAccept(30_000_000, 10_000_000, out _), "future source timestamp cannot poison the media clock");
Check(invalidSource.TryAccept(10_000_000, 10_000_000, out var validFirst) && validFirst == 0, "valid frame after invalid future timestamp starts normally");
Check(!invalidSource.TryAccept(9_000_000, 10_000_000, out _), "source clock regression is dropped");
Check(invalidSource.TryAccept(11_000_000, 11_000_000, out var validNext) && validNext == 1_000_000, "invalid timestamp does not mutate pacing state");
var diagnostics = new VideoFrameTiming(15);
diagnostics.TryAccept(30_000_000, 10_000_000, out _);
Check(diagnostics.LastDropReason == VideoFrameDropReason.FutureTimestamp, "future timestamp rejection reports precise reason");
diagnostics.TryAccept(1_000_000, 10_000_000, out _);
Check(diagnostics.LastDropReason == VideoFrameDropReason.Stale, "stale capture rejection reports precise reason");
diagnostics.TryAccept(10_000_000, 10_000_000, out _);
Check(diagnostics.LastDropReason == VideoFrameDropReason.None, "accepted capture clears previous rejection reason");
diagnostics.TryAccept(10_000_001, 10_000_001, out _);
Check(diagnostics.LastDropReason == VideoFrameDropReason.Cadence, "cadence rejection reports precise reason");
var compositorTiming = new VideoFrameTiming(15);
Check(compositorTiming.TryAccept(10_100_000, 10_000_000, out var compositorFirst) && compositorFirst == 0,
    "real WGC frame timestamp10ms ahead of callback is accepted");
var compositorAccepted = 0;
var compositorCadence = new VideoFrameTiming(15);
for (var index = 0; index < 600; index++)
{
    var now = 10_000_000L + index * 10_000_000L / 60;
    if (compositorCadence.TryAccept(now + 100_000, now, out _)) compositorAccepted++;
}
Check(compositorAccepted >= 149 && compositorAccepted <= 151, "real compositor timestamp lead sustains15fps instead of starving capture");
var thirtyFpsTiming = new VideoFrameTiming(30);
var thirtyFpsAccepted = 0;
for (var index = 0; index < 600; index++)
{
    var now = 10_000_000L + index * 10_000_000L / 60;
    if (thirtyFpsTiming.TryAccept(now + 100_000, now, out _)) thirtyFpsAccepted++;
}
Check(thirtyFpsAccepted >= 299 && thirtyFpsAccepted <= 301, "60Hz WGC input with10ms presentation lead sustains30fps");
Check(!thirtyFpsTiming.TryAccept(120_000_000, 119_000_000, out _), "30fps still rejects100ms future clock skew");
Check(!thirtyFpsTiming.TryAccept(111_000_000, 120_000_000, out _), "30fps still rejects stale queued frame");
Check(thirtyFpsTiming.TryAccept(120_100_000, 120_000_000, out var afterThirtyFpsGap) && afterThirtyFpsGap == 110_000_000,
    "30fps preserves actual11second source gap without synthetic timeline compression");
Check(!thirtyFpsTiming.TryAccept(120_110_000, 120_010_000, out _), "30fps does not burst immediately after gap");
var sixtyTiming = new VideoFrameTiming(60);
var sixtyAccepted = 0;
for (var index = 0; index < 600; index++)
{
    var now = 10_000_000L + index * 10_000_000L / 60;
    if (sixtyTiming.TryAccept(now + 100_000, now, out _)) sixtyAccepted++;
}
Check(sixtyAccepted >= 598 && sixtyAccepted <= 600, "60Hz input with10ms compositor lead sustains60fps");
Check(!sixtyTiming.TryAccept(120_000_000, 119_000_000, out _), "60fps rejects100ms future skew");
Check(sixtyTiming.TryAccept(120_100_000, 120_000_000, out var sixtyGap) && sixtyGap == 110_000_000, "60fps retains source gap");
Check(!sixtyTiming.TryAccept(120_110_000, 120_010_000, out _), "60fps does not burst after source gap");
var packets = new List<(byte[] Payload, uint Timestamp, int Marker, int Type)>();
void Send(byte[] data, uint timestamp, int marker, int type) => packets.Add((data, timestamp, marker, type));
var smallNal = new byte[] { 0x67, 1, 2, 3 };
H264RtpSender.SendNal(smallNal, false, 9000, 100, Send);
Check(packets.Count == 1 && packets[0].Payload.SequenceEqual(smallNal) && packets[0].Marker == 0, "single NAL preserved with no marker before last access-unit NAL");
packets.Clear();
H264RtpSender.SendNal(new byte[1200], true, 90000, 100, Send);
Check(packets.Count == 1 && packets[0].Payload.Length == 1200 && packets[0].Marker == 1, "1200 byte NAL stays single packet and marks final access-unit NAL");
packets.Clear();
var largeNal = Enumerable.Range(0, 3600).Select(index => (byte)index).ToArray();
largeNal[0] = 0x65;
H264RtpSender.SendNal(largeNal, true, 0xfffffff0, 103, Send);
Check(packets.Count == 4 && packets.All(packet => packet.Payload.Length <= 1200), "FU-A fragments stay within1200 byte RTP payload limit");
Check(packets[0].Payload[0] == 0x7c && packets[0].Payload[1] == 0x85 && packets[1].Payload[1] == 0x05 && packets[^1].Payload[1] == 0x45, "FU-A indicator and start/end header bytes preserve NRI and NAL type");
Check(packets.All(packet => packet.Timestamp == 0xfffffff0 && packet.Type == 103), "every fragment carries exact explicit timestamp and negotiated payload type");
Check(packets.Take(packets.Count - 1).All(packet => packet.Marker == 0) && packets[^1].Marker == 1, "only final fragment of final access-unit NAL has RTP marker");
Check(packets.SelectMany(packet => packet.Payload.Skip(2)).SequenceEqual(largeNal.Skip(1)), "fragment reassembly preserves every encoded NAL payload byte");
packets.Clear();
H264RtpSender.SendNal(largeNal, false, 180000, 100, Send);
Check(packets.All(packet => packet.Marker == 0) && (packets[^1].Payload[1] & 0x40) != 0, "non-final large NAL ends FU-A without ending access unit");
Console.WriteLine($"{passed} media regression checks passed");


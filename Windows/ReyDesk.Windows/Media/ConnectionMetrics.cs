namespace ReyDesk.Windows.Media;

// Measurements describe the receiver/media path, not Internet bandwidth or
// capture-to-display latency. Null means no valid measurement is available.
internal sealed record ConnectionMetrics(
    double? Fps, double? BitrateKbps, double? RoundTripTimeMs,
    double? PacketLossPercent, double? JitterMs, int Width, int Height,
    long FramesDropped);

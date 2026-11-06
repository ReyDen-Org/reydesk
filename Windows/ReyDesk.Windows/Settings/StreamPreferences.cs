using System;

namespace ReyDesk.Windows.Settings;

internal sealed record StreamPreferences(int MaxHeight = 1080, int MaxFps = 60, int BitrateKbps = 12000)
{
    public static StreamPreferences Normalize(int maxHeight, int maxFps, int bitrateKbps) => new(
        maxHeight is 540 or 720 or 1080 or 1440 or 2160 ? maxHeight : 1080,
        maxFps is 15 or 30 or 60 ? maxFps : 60,
        bitrateKbps is 4000 or 8000 or 12000 or 20000 or 30000 or 50000 ? bitrateKbps : 12000);

    // Unknown/omitted receiver fields inherit the host setting, never lift a cap.
    public StreamPreferences LimitRequest(int height, int fps, int bitrateKbps) => new(
        height is 540 or 720 or 1080 or 1440 or 2160 ? Math.Min(MaxHeight, height) : MaxHeight,
        fps is 15 or 30 or 60 ? Math.Min(MaxFps, fps) : MaxFps,
        bitrateKbps is 4000 or 8000 or 12000 or 20000 or 30000 or 50000 ? Math.Min(BitrateKbps, bitrateKbps) : BitrateKbps);
}

using System;

namespace ReyDesk.Windows.Media;

internal static class VideoCaptureQuality
{
    public static (int Width, int Height) GetEncodeSize(int width, int height, int maxHeight = 1080)
    {
        // Preserve native1080p desktop text; keep larger monitors bounded rather
        // than letting4K pixel work jeopardize interactive capture latency.
        var boundedHeight = maxHeight is 540 or 720 or 1080 or 1440 or 2160 ? maxHeight : 1080;
        var longestSide = boundedHeight * (16.0 / 9.0);
        var scale = Math.Min(1.0, Math.Min(longestSide / Math.Max(width, height),
            Math.Sqrt((longestSide * boundedHeight) / ((double)width * height))));
        return ((int)(width * scale) & ~1, (int)(height * scale) & ~1);
    }
}

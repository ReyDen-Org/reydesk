using System;

namespace ReyDesk.Windows.Media;

/// <summary>
/// BT.601 BGRA -> NV12 conversion (with an optional downscale in the same pass, see
/// ScreenCapture's EncodeWidth/EncodeHeight), done on the CPU after reading the
/// captured frame back from the GPU (see ScreenCapture's staging-texture copy). A
/// GPU-side conversion (video processor MFT or a compute shader) would avoid this
/// readback entirely and is the natural next optimization if CPU cost here still
/// proves to be a bottleneck even after downscaling - not attempted yet, see
/// H264Encoder's class doc comment for the same tradeoff on the encoder side.
/// </summary>
internal static unsafe class Nv12Conversion
{
    /// <summary>
    /// Converts a BGRA8 source (as mapped from a D3D11 staging texture, so rows may be
    /// padded beyond srcWidth*4 - hence the separate <paramref name="srcStrideBytes"/>)
    /// into <paramref name="nv12"/>, a tightly-packed buffer of exactly
    /// dstWidth*dstHeight*3/2 bytes, resampling from src to dst dimensions if they
    /// differ (nearest-neighbour). Native1080p avoids discarding fine text strokes
    /// before client zoom. Chroma is additionally2x2 subsampled. dstWidth/dstHeight
    /// must both be even (NV12's 4:2:0 chroma requirement).
    /// </summary>
    public static void ConvertBgraToNv12(byte* bgra, int srcStrideBytes, int srcWidth, int srcHeight, int dstWidth, int dstHeight, byte[] nv12)
    {
        var ySize = dstWidth * dstHeight;
        fixed (byte* nv12Ptr = nv12)
        {
            if (srcWidth == dstWidth && srcHeight == dstHeight)
            {
                ConvertNative(bgra, srcStrideBytes, dstWidth, dstHeight, nv12Ptr);
                return;
            }
            for (var dy = 0; dy < dstHeight; dy++)
            {
                var sy = dy * srcHeight / dstHeight;
                var rowIn = bgra + sy * srcStrideBytes;
                var rowOut = nv12Ptr + dy * dstWidth;
                for (var dx = 0; dx < dstWidth; dx++)
                {
                    var sx = dx * srcWidth / dstWidth;
                    var p = rowIn + sx * 4;
                    int b = p[0], g = p[1], r = p[2];
                    rowOut[dx] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                }
            }

            for (var dy = 0; dy < dstHeight; dy += 2)
            {
                var sy = dy * srcHeight / dstHeight;
                var rowIn = bgra + sy * srcStrideBytes;
                var uvRowOut = nv12Ptr + ySize + (dy / 2) * dstWidth;
                for (var dx = 0; dx < dstWidth; dx += 2)
                {
                    var sx = dx * srcWidth / dstWidth;
                    var p = rowIn + sx * 4;
                    int b = p[0], g = p[1], r = p[2];
                    uvRowOut[dx] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    uvRowOut[dx + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            }
        }
    }

    private static void ConvertNative(byte* bgra, int stride, int width, int height, byte* nv12)
    {
        var chroma = nv12 + width * height;
        // A native-size2x2 block requires no division/resampling, and its top-left
        // pixel supplies chroma just as in the existing conversion path.
        for (var y = 0; y < height; y += 2)
        {
            var first = bgra + y * stride;
            var second = first + stride;
            var yFirst = nv12 + y * width;
            var ySecond = yFirst + width;
            var uv = chroma + y / 2 * width;
            for (var x = 0; x < width; x += 2)
            {
                var top = first + x * 4;
                var bottom = second + x * 4;
                yFirst[x] = (byte)(((66 * top[2] + 129 * top[1] + 25 * top[0] + 128) >> 8) + 16);
                yFirst[x + 1] = (byte)(((66 * top[6] + 129 * top[5] + 25 * top[4] + 128) >> 8) + 16);
                ySecond[x] = (byte)(((66 * bottom[2] + 129 * bottom[1] + 25 * bottom[0] + 128) >> 8) + 16);
                ySecond[x + 1] = (byte)(((66 * bottom[6] + 129 * bottom[5] + 25 * bottom[4] + 128) >> 8) + 16);
                int b = top[0], g = top[1], r = top[2];
                // BT.601 limited-range results for8-bit RGB fit in a byte.
                uv[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                uv[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }

}

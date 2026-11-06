using ReyDesk.Windows.Media;
using ReyDesk.Windows.Settings;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
Check(VideoCaptureQuality.GetEncodeSize(1920, 1080) == (1920, 1080), "native1080p text retains every source pixel before zoom");
Check(VideoCaptureQuality.GetEncodeSize(1920, 1080, 720) == (1280, 720), "720p setting changes actual capture dimensions");
Check(VideoCaptureQuality.GetEncodeSize(1920, 1080, 540) == (960, 540), "540p setting changes actual capture dimensions");
Check(VideoCaptureQuality.GetEncodeSize(3840, 2160, 2160) == (3840, 2160), "2160p setting keeps native 4K");
Check(VideoCaptureQuality.GetEncodeSize(3840, 2160, 1440) == (2560, 1440), "1440p setting downscales 4K to 1440p");
Check(StreamPreferences.Normalize(2160, 60, 50000) == new StreamPreferences(2160, 60, 50000), "2160p and 50 Mbps are accepted");
Check(StreamPreferences.Normalize(1440, 30, 30000) == new StreamPreferences(1440, 30, 30000), "1440p and 30 Mbps are accepted");
Check(new StreamPreferences(2160, 60, 50000).LimitRequest(1440, 30, 30000) == new StreamPreferences(1440, 30, 30000), "receiver may request below the new ceilings");
Check(new StreamPreferences(1080, 60, 12000).LimitRequest(2160, 60, 50000) == new StreamPreferences(1080, 60, 12000), "receiver cannot exceed 1080p/12 Mbps host ceiling with new values");
var defaults = new StreamPreferences();
Check(defaults.LimitRequest(0, 0, 0) == defaults, "older peers inherit host native1080p60 defaults");
var capped = new StreamPreferences(720, 30, 8000);
Check(capped.LimitRequest(1080, 60, 20000) == capped, "receiver cannot raise any host ceiling");
Check(defaults.LimitRequest(540, 15, 4000) == new StreamPreferences(540, 15, 4000), "lower receiver preferences are applied");
Check(capped.LimitRequest(int.MinValue, int.MaxValue, -1) == capped, "malicious out-of-range preferences inherit safe ceilings");
Check(StreamPreferences.Normalize(9999, 1000, -500) == defaults, "invalid persisted preferences restore bounded defaults");
var sessionSnapshot = capped;
capped = new StreamPreferences(1080, 60, 12000);
Check(sessionSnapshot == new StreamPreferences(720, 30, 8000), "changing saved settings leaves existing session snapshot unchanged");
Check(VideoCaptureQuality.GetEncodeSize(3840, 2160) == (1920, 1080), "4K remains bounded to1080p pixel workload");
Check(VideoCaptureQuality.GetEncodeSize(1080, 1920) == (1080, 1920), "portrait1080p retains native detail");
Check(VideoCaptureQuality.GetEncodeSize(1280, 720) == (1280, 720), "small displays are never enlarged before encoding");
var odd = VideoCaptureQuality.GetEncodeSize(1919, 1079);
Check(odd == (1918, 1078), "odd dimensions round down for NV12 chroma");
var ultraWide = VideoCaptureQuality.GetEncodeSize(3440, 1440);
Check(ultraWide.Width == 1920 && ultraWide.Height % 2 == 0 && ultraWide.Width * ultraWide.Height <= 1920 * 1080,
    "ultrawide remains within1080p pixel budget with even chroma dimensions");
var square = VideoCaptureQuality.GetEncodeSize(2560, 2560);
Check(square.Width * square.Height <= 1920 * 1080, "square monitors cannot exceed1080p pixel workload");

unsafe byte[] Convert(byte[] source, int stride, int sw, int sh, int dw, int dh)
{
    var output = new byte[dw * dh * 3 / 2];
    fixed (byte* pointer = source) Nv12Conversion.ConvertBgraToNv12(pointer, stride, sw, sh, dw, dh, output);
    return output;
}
byte[] Reference(byte[] source, int stride, int sw, int sh, int dw, int dh)
{
    var result = new byte[dw * dh * 3 / 2];
    for (var y = 0; y < dh; y++) for (var x = 0; x < dw; x++)
    {
        var p = (y * sh / dh) * stride + x * sw / dw * 4;
        int b = source[p], g = source[p + 1], r = source[p + 2];
        result[y * dw + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
        if ((x & 1) != 0 || (y & 1) != 0) continue;
        var uv = dw * dh + y / 2 * dw + x;
        result[uv] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
        result[uv + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
    }
    return result;
}
var random = new Random(42);
var source = new byte[(32 * 4 + 12) * 24];
random.NextBytes(source);
Check(Convert(source, 140, 32, 24, 32, 24).SequenceEqual(Reference(source, 140, 32, 24, 32, 24)),
    "native conversion preserves exact BT601 bytes and respects padded source rows");
Check(Convert(source, 140, 32, 24, 16, 12).SequenceEqual(Reference(source, 140, 32, 24, 16, 12)),
    "downscale conversion preserves previous sampling and chroma behavior");
var text = new byte[8 * 8 * 4];
for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
{
    if (x != 3 && y != 3) continue;
    var p = (y * 8 + x) * 4;
    text[p] = text[p + 1] = text[p + 2] = 255;
}
var luminance = Convert(text, 32, 8, 8, 8, 8);
Check(Enumerable.Range(0, 64).All(index => luminance[index] == (index % 8 == 3 || index / 8 == 3 ? 235 : 16)),
    "native conversion retains one-pixel text strokes in luminance");
Console.WriteLine($"{passed} media quality checks passed");

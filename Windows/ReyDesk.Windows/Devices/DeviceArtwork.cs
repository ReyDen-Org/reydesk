namespace ReyDesk.Windows.Devices;

/// <summary>
/// Model-agnostic device artwork data. The artwork itself is drawn in code (see MainPage.CreateDeviceArtwork);
/// this class only decides the silhouette kind and a stable tint, so any phone or tablet renders the same way.
/// </summary>
internal static class DeviceArtwork
{
    internal enum DeviceKind { Phone, Tablet }

    /// <summary>The name shown to the user: the reported model string, never a guessed marketing name.</summary>
    internal static string GetDisplayName(string? model) => model ?? string.Empty;

    /// <summary>Tablet silhouette when the reported name looks like a tablet, otherwise phone.</summary>
    internal static DeviceKind GetKind(string? model)
    {
        var key = (model ?? string.Empty).Trim().ToUpperInvariant();
        return key.Contains("TAB") || key.Contains("PAD") || key.Contains("TABLET") ? DeviceKind.Tablet : DeviceKind.Phone;
    }

    /// <summary>Width / height of the silhouette.</summary>
    internal static double GetAspect(DeviceKind kind) => kind == DeviceKind.Tablet ? 0.74 : 0.5;

    /// <summary>Stable hue (0-359) from a device id (FNV-1a), identical across runs and machines.</summary>
    internal static int GetHue(string? seed)
    {
        var hash = 2166136261u;
        foreach (var ch in seed ?? string.Empty)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return (int)(hash % 360u);
    }

    /// <summary>HSL (h 0-359, s and l 0-1) to RGB bytes.</summary>
    internal static (byte R, byte G, byte B) HslToRgb(int hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = (hue % 360 + 360) % 360 / 60.0;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = lightness - c / 2;
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

using ReyDesk.Windows.Devices;

var count = 0;
void Check(bool ok, string name)
{
    count++;
    if (!ok) { Console.Error.WriteLine("FAIL: " + name); Environment.Exit(1); }
}

Check(DeviceArtwork.GetKind("SM-A325F") == DeviceArtwork.DeviceKind.Phone, "phone model is a phone");
Check(DeviceArtwork.GetKind("Unknown phone") == DeviceArtwork.DeviceKind.Phone, "unknown model defaults to phone");
Check(DeviceArtwork.GetKind(null) == DeviceArtwork.DeviceKind.Phone, "missing model defaults to phone");
Check(DeviceArtwork.GetKind("Galaxy Tab S9") == DeviceArtwork.DeviceKind.Tablet, "tab name is a tablet");
Check(DeviceArtwork.GetKind("Lenovo Pad Pro") == DeviceArtwork.DeviceKind.Tablet, "pad name is a tablet");
Check(DeviceArtwork.GetDisplayName("../../other.png") == "../../other.png", "display name is the reported text only");
Check(DeviceArtwork.GetDisplayName(null) == "", "missing model has empty display name");
Check(DeviceArtwork.GetHue("abc") == DeviceArtwork.GetHue("abc"), "hue is stable");
Check(DeviceArtwork.GetHue("device-1") != DeviceArtwork.GetHue("device-2"), "different ids usually differ");
Check(DeviceArtwork.GetHue("abc") is >= 0 and < 360, "hue in range");
Check(DeviceArtwork.GetHue(null) is >= 0 and < 360, "null seed is safe");
Check(DeviceArtwork.HslToRgb(0, 1, .5) == (255, 0, 0), "red conversion");
Check(DeviceArtwork.HslToRgb(120, 1, .5) == (0, 255, 0), "green conversion");
Check(DeviceArtwork.GetAspect(DeviceArtwork.DeviceKind.Tablet) > DeviceArtwork.GetAspect(DeviceArtwork.DeviceKind.Phone), "tablet is wider");
Console.WriteLine($"{count} device artwork checks passed");

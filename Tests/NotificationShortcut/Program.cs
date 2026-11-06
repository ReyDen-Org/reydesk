using ReyDesk.Windows.Platform;

var temporary = Path.Combine(Path.GetTempPath(), "ReyDesk-shortcut-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var shortcut = Path.Combine(temporary, "ReyDesk.lnk");
var target = Environment.ProcessPath!;
var activator = Guid.NewGuid();
var checks = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
try
{
    NotificationShortcut.SetProcessIdentity("Reyden.ReyDesk.ShortcutTest");
    Check(NotificationShortcut.GetProcessIdentity() == "Reyden.ReyDesk.ShortcutTest", "Explicit process identity round trip");
    NotificationShortcut.Ensure(shortcut, target, "Reyden.ReyDesk.ShortcutTest", activator);
    var read = NotificationShortcut.Read(shortcut);
    Check(string.Equals(read.Target, target, StringComparison.OrdinalIgnoreCase), "Target round trip");
    Check(read.AppId == "Reyden.ReyDesk.ShortcutTest", "App identity round trip");
    Check(read.Activator == activator, "Activator GUID round trip");
    var written = File.GetLastWriteTimeUtc(shortcut);
    NotificationShortcut.Ensure(shortcut, target, read.AppId, activator);
    Check(File.GetLastWriteTimeUtc(shortcut) == written, "Matching shortcut is not rewritten");
    try { NotificationShortcut.Ensure(shortcut, target, "Another.Application", activator); throw new Exception("Foreign identity overwritten"); }
    catch (InvalidOperationException) { checks++; }
    Check(NotificationShortcut.Read(shortcut).AppId == read.AppId, "Foreign identity attempt preserves file");
    Console.WriteLine($"PASS {checks} shortcut registration checks");
}
finally { if (File.Exists(shortcut)) File.Delete(shortcut); Directory.Delete(temporary); }


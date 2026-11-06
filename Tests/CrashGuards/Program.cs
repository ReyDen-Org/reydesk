// Source guards for the 2026-11 crash. WinUI types cannot load in a plain net8.0 test,
// so these check the source tree: no raw DispatcherQueue.TryEnqueue off the UI-safe helper, global handlers present,
// and the version comes from ReyDesk/VERSION only.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var win = Path.Combine(root, "Windows", "ReyDesk.Windows");
var failures = 0;
void Check(bool ok, string label) { Console.WriteLine((ok ? "ok   " : "FAIL ") + label); if (!ok) failures++; }

var sources = Directory.EnumerateFiles(win, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToArray();

var raw = sources.Where(f => !f.EndsWith("UiDispatch.cs") && File.ReadAllText(f).Contains(".TryEnqueue(")).Select(Path.GetFileName).ToArray();
Check(raw.Length == 0, "no raw .TryEnqueue( outside UiDispatch.cs" + (raw.Length > 0 ? ": " + string.Join(", ", raw) : ""));

var app = File.ReadAllText(Path.Combine(win, "App.xaml.cs"));
Check(app.Contains("UnobservedTaskException") && app.Contains("SetObserved()"), "unobserved task exceptions are logged and observed");
Check(app.Contains("CurrentDomain.UnhandledException"), "AppDomain unhandled exceptions are logged");
Check(app.Contains("this.UnhandledException") && app.Contains("e.Handled ="), "XAML unhandled exceptions are logged and survived");

var dispatch = File.ReadAllText(Path.Combine(win, "Platform", "UiDispatch.cs"));
Check(dispatch.Contains("catch (Exception"), "UiDispatch swallows and logs dispatcher failures");

var hard = sources.Where(f => File.ReadAllText(f).Contains("0.2.0-dev")).Select(Path.GetFileName).ToArray();
Check(hard.Length == 0, "no hard-coded 0.2.0-dev in Windows sources");

var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
Check(System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"), $"VERSION is plain semver ({version})");
Check(!string.IsNullOrWhiteSpace(ReyDesk.Windows.Platform.AppInfo.Version), "AppInfo.Version is not empty");

Console.WriteLine(failures == 0 ? "PASS CrashGuards" : $"FAILED {failures}");
return failures == 0 ? 0 : 1;

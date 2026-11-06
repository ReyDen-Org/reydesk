using System.Reflection;
using System.Runtime.InteropServices;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.PhoneHub;
using ReyDesk.Windows.Platform;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
PhoneNotification Message(string id = "sms", long time = 1, string text = "one", string revision = "a") => new() { Id = id, PostedAtUnixMs = time, Text = text, Title = "Contact", Revision = revision };
var gate = new PhoneAlertGate();
var original = Message();
Check(gate.Observe(null, original, true, 0), "First live message eligible");
Check(!gate.Observe(null, original, true, 31000), "Exact replay suppressed");
var refresh = Message(revision: "random-handle");
Check(!gate.Observe(original, refresh, true, 31000), "Action revision refresh suppressed");
Check(!gate.Observe(original, Message(time: 2, revision: "b"), true, 31000), "Newer metadata without new content suppressed");
Check(!gate.Observe(original, Message(text: "edited", revision: "c"), true, 31000), "Same-time edit suppressed");
Check(!gate.Observe(original, Message(time: 3, text: "two"), true, 31000), "Unchanged native revision suppressed");
var incoming = Message(time: 4, text: "next SMS", revision: "d");
Check(gate.Observe(original, incoming, true, 31000), "Same conversation newer incoming SMS eligible");
Check(!gate.Observe(original, incoming, true, 62000), "New-message replay suppressed");
Check(!gate.Observe(null, Message("other"), true, 32000), "Conservative 30-second rate limit");
Check(!gate.Observe(null, Message("other"), true, 65000), "Rate-limited message never replayed later");
foreach (var context in new[] { "snapshot", "focus", "toggle-off", "other-device", "no-permission" })
{
    var suppressed = new PhoneAlertGate();
    Check(!suppressed.Observe(null, Message(context), false, 0), context + " suppressed");
    Check(!suppressed.Observe(null, Message(context), true, 31000), context + " never replayed after enabling");
}
var redacted = Message(time: 5, text: "secret", revision: "e"); redacted.Redacted = true;
Check(!gate.Observe(incoming, redacted, true, 65000), "Redaction update never alerts");
var redactedGate = new PhoneAlertGate();
Check(redactedGate.Observe(null, redacted, true, 0), "Fresh private notification may alert generically");
var privateNext = redacted.Clone(); privateNext.PostedAtUnixMs++; privateNext.Revision = "private-next";
Check(redactedGate.Observe(redacted, privateNext, true, 31000), "Default-redacted same conversation newer SMS eligible");
var privateRefresh = privateNext.Clone(); privateRefresh.Revision = "refresh";
Check(!redactedGate.Observe(privateNext, privateRefresh, true, 62000), "Redacted same-time refresh suppressed");
var reveal = redacted.Clone(); reveal.Redacted = false; reveal.PostedAtUnixMs++; reveal.Revision = "f";
Check(!redactedGate.Observe(redacted, reveal, true, 31000), "Preview permission change never alerts");
var saturated = new PhoneAlertGate();
for (int i = 0; i < 2048; i++) saturated.Observe(i.ToString(), "revision", false, i);
Check(!saturated.Observe("overflow", "revision", true, 100000), "Bounded state fails closed at capacity");
Check(new PhoneAlertGate().Observe(null, original, true, 0), "New session has separate gate");

// Reflection only: no HWND, shell operation, notification, or process launch in these tests.
var layout = typeof(TrayIcon).GetNestedType("NOTIFYICONDATA", BindingFlags.NonPublic)!;
var wide = IntPtr.Size == 8;
Check(Marshal.SizeOf(layout) == (wide ? 976 : 956), "Full Unicode NOTIFYICONDATA size");
foreach (var offset in new[] { ("szInfo", wide ? 304 : 288), ("uTimeoutOrVersion", wide ? 816 : 800), ("szInfoTitle", wide ? 820 : 804), ("dwInfoFlags", wide ? 948 : 932), ("guidItem", wide ? 952 : 936), ("hBalloonIcon", wide ? 968 : 952) })
    Check(Marshal.OffsetOf(layout, offset.Item1).ToInt32() == offset.Item2, "Win32 offset " + offset.Item1);
Check(typeof(TrayIcon).GetMethod("ShowPhoneHubAlert")!.GetParameters().Length == 0, "Shell API accepts no remote content");
Check(typeof(TrayIcon).GetMethod("ShowPhoneHubTestAlert") is { } testAlert && testAlert.GetParameters().Length == 0,
    "Synthetic notification test accepts no remote content");
var stateStatus = typeof(TrayIcon).GetMethod("NotificationStateStatus", BindingFlags.Static | BindingFlags.Public);
Check(stateStatus is not null, "Windows suppression state has an explicit status");
foreach (var pair in new[] { (1, "hub.testNotPresent"), (2, "hub.testBusy"), (3, "hub.testBusy"), (4, "hub.testBusy"), (5, "hub.testSubmitted"), (6, "hub.testQuiet"), (7, "hub.testBusy"), (0, "hub.testFailed"), (99, "hub.testFailed") })
    Check((string?)stateStatus!.Invoke(null, new object[] { pair.Item1 }) == pair.Item2, "Notification state " + pair.Item1);
foreach (var language in Enum.GetValues<ReyDesk.Windows.Settings.AppLanguage>())
{
    ReyDesk.Windows.Localization.Strings.Current = language;
    Check(ReyDesk.Windows.Localization.Strings.Get("hub.shellTitle").Length is > 0 and < 64, language + " fixed shell title bounds");
    Check(ReyDesk.Windows.Localization.Strings.Get("hub.shellMessage").Length is > 0 and < 256, language + " fixed shell message bounds");
    Check(ReyDesk.Windows.Localization.Strings.Get("hub.shellAlerts") != "hub.shellAlerts", language + " toggle translated");
    Check(ReyDesk.Windows.Localization.Strings.Get("hub.shellTestTitle").Length is > 0 and < 64 &&
        ReyDesk.Windows.Localization.Strings.Get("hub.shellTestTitle") != "hub.shellTestTitle", language + " explicit test title bounds");
    Check(ReyDesk.Windows.Localization.Strings.Get("hub.shellTestMessage").Length is > 0 and < 256 &&
        ReyDesk.Windows.Localization.Strings.Get("hub.shellTestMessage") != "hub.shellTestMessage", language + " explicit test body bounds");
    foreach (var key in new[] { "hub.testBusy", "hub.testQuiet", "hub.testNotPresent", "hub.testDisabled" })
        Check(ReyDesk.Windows.Localization.Strings.Get(key) != key, language + " suppression reason " + key);
}
Console.WriteLine($"PASS {passed}: Phone Hub tray alert policy and native structure; no shell/UI calls.");

namespace ReyDesk.Windows.Settings
{
    internal enum AppLanguage { English, Persian }
}

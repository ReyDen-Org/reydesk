using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using ReyDesk.Windows.Localization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ReyDesk.Windows.Platform;

// Only fixed local notices enter Windows. Phone content remains inside ReyDesk.
internal sealed class PhoneHubWindowsAlerts : IDisposable
{
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    private bool _registered;
    private bool _shortcutReady;
    private const string AppId = "Reyden.ReyDesk";
    private uint _lastId;
    public string Status { get; private set; } = "hub.testFailed";
    public event Action? OpenRequested;

    public bool Initialize()
    {
        try
        {
            if (!_registered)
            {
                // Must precede the first access to Default: the SDK caches its app ID.
                NotificationShortcut.SetProcessIdentity(AppId);
                AppNotificationManager.Default.NotificationInvoked += OnInvoked;
                AppNotificationManager.Default.Register("ReyDesk", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png")));
                _registered = true;
            }
            EnsureToastIcon();
            // Register() already wrote the AppUserModelId registry identity, which is what
            // unpackaged App SDK toasts need. The Start shortcut is an extra, so it never blocks alerts.
            if (!_shortcutReady) _shortcutReady = TryEnsureShortcut();
            return true;
        }
        catch (Exception error) { Log("Windows sender registration", error); return false; }
    }

    /// <summary>The toast header icon comes from this registry value, which another copy of the app may have left pointing at its own (older) logo.</summary>
    private static void EnsureToastIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png");
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            if (!string.Equals(key.GetValue("IconUri") as string, icon, StringComparison.OrdinalIgnoreCase)) key.SetValue("IconUri", icon, RegistryValueKind.ExpandString);
        }
        catch (Exception error) { Log("Toast icon registry (non-fatal)", error); }
    }

    private static bool TryEnsureShortcut()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ReyDesk.lnk");
        // (path may be redirected below to a per-copy shortcut)
        var exe = Environment.ProcessPath!;
        byte[]? original = null;
        try
        {
            using var identity = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            if (!Guid.TryParse(identity?.GetValue("CustomActivator") as string, out var activator))
            {
                Console.WriteLine("[PhoneHub] No CustomActivator under the AppUserModelId key; Start shortcut skipped.");
                return false;
            }
            if (File.Exists(path))
            {
                var existing = NotificationShortcut.Read(path);
                if (existing.AppId != AppId)
                {
                    // The Inno installer's shortcut carries no AppUserModelID. Upgrade it only when it
                    // starts this same exe; never retarget the installed shortcut to another copy.
                    if (existing.AppId.Length != 0 || !string.Equals(existing.Target, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        // Another copy (installed build) owns the main shortcut. Windows only shows toasts for an
                        // unpackaged exe that has a Start shortcut with its identity, so give this copy its own.
                        Console.WriteLine($"[PhoneHub] Start shortcut belongs to another copy (id='{existing.AppId}'); creating a shortcut for this copy.");
                        path = Path.Combine(Path.GetDirectoryName(path)!, "ReyDesk (this copy).lnk");
                        if (File.Exists(path) && !string.Equals(NotificationShortcut.Read(path).Target, exe, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                        if (File.Exists(path)) return true;
                        NotificationShortcut.Ensure(path, exe, AppId, activator);
                        return true;
                    }
                    original = File.ReadAllBytes(path);
                    File.Delete(path);
                }
            }
            NotificationShortcut.Ensure(path, exe, AppId, activator);
            Console.WriteLine("[PhoneHub] Windows sender identity and Start shortcut verified.");
            return true;
        }
        catch (Exception error)
        {
            if (original != null && !File.Exists(path))
            {
                try { File.WriteAllBytes(path, original); }
                catch (Exception restore) { Log("Start shortcut restore", restore); }
            }
            Log("Start shortcut (non-fatal)", error);
            return false;
        }
    }

    private static void Log(string stage, Exception error) =>
        Console.WriteLine($"[PhoneHub] {stage} HRESULT=0x{error.HResult:X8} type={error.GetType().Name} message={error.Message}{Environment.NewLine}{error.StackTrace}");

    public bool ShowTest() => Show(test: true);
    public bool ShowPhoneNotice() => Show(test: false);
    private bool Show(bool test)
    {
        Status = "hub.testFailed";
        try
        {
            var queried = SHQueryUserNotificationState(out var state) == 0;
            // The standard notification API applies Windows display policy itself.
            // Interruptibility is diagnostic, not proof that this toast is blocked.
            if (!Initialize()) return false;
            var setting = AppNotificationManager.Default.Setting;
            Console.WriteLine($"[PhoneHub] Windows notification setting={setting}; interruptibility={(queried ? state : 0)}.");
            // Only an explicit "disabled" answer blocks the alert. Unpackaged apps often report Unsupported even though toasts work, so try anyway.
            if (setting is AppNotificationSetting.DisabledForApplication or AppNotificationSetting.DisabledForUser
                or AppNotificationSetting.DisabledByGroupPolicy or AppNotificationSetting.DisabledByManifest)
            { Status = "hub.testDisabled"; return false; }
            var notice = new AppNotificationBuilder().AddArgument("action", "phonehub")
                .AddText(Strings.Get(test ? "hub.shellTestTitle" : "hub.shellTitle"))
                .AddText(Strings.Get(test ? "hub.shellTestMessage" : "hub.shellMessage"))
                .MuteAudio().BuildNotification();
            notice.Expiration = DateTimeOffset.UtcNow.AddMinutes(test ? 5 : 1);
            notice.Tag = "phonehub";
            notice.Group = "phonehub";
            AppNotificationManager.Default.Show(notice);
            _lastId = notice.Id;
            Console.WriteLine($"[PhoneHub] Standard Windows notification accepted={_lastId != 0}.");
            if (_lastId == 0) return ShowClassic(test);
            Status = "hub.testSubmitted";
            _ = CheckQueuedAsync(_lastId);
            return true;
        }
        catch (Exception error) { Log("Windows notification failure", error); return false; }
    }
    /// <summary>Fallback: the classic toast API with the same AppUserModelID (works for desktop apps that have a Start shortcut with that ID).</summary>
    private bool ShowClassic(bool test)
    {
        try
        {
            var notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId);
            Console.WriteLine($"[PhoneHub] Classic toast notifier setting={notifier.Setting}.");
            if (notifier.Setting != global::Windows.UI.Notifications.NotificationSetting.Enabled)
            {
                Status = notifier.Setting == global::Windows.UI.Notifications.NotificationSetting.DisabledForUser
                    || notifier.Setting == global::Windows.UI.Notifications.NotificationSetting.DisabledForApplication
                    ? "hub.testDisabled" : "hub.testFailed";
                return false;
            }
            var xml = new global::Windows.Data.Xml.Dom.XmlDocument();
            string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
            xml.LoadXml("<toast launch=\"action=phonehub\"><visual><binding template=\"ToastGeneric\"><text>" +
                Esc(Strings.Get(test ? "hub.shellTestTitle" : "hub.shellTitle")) + "</text><text>" +
                Esc(Strings.Get(test ? "hub.shellTestMessage" : "hub.shellMessage")) + "</text></binding></visual><audio silent=\"true\"/></toast>");
            var toast = new global::Windows.UI.Notifications.ToastNotification(xml) { Tag = "phonehub", Group = "phonehub", ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(test ? 5 : 1) };
            notifier.Show(toast);
            Console.WriteLine("[PhoneHub] Classic Windows toast submitted.");
            Status = "hub.testSubmitted";
            return true;
        }
        catch (Exception error) { Log("Classic Windows toast", error); Status = "hub.testFailed"; return false; }
    }
    private static async Task CheckQueuedAsync(uint id)
    {
        try
        {
            await Task.Delay(300);
            var ownNotices = await AppNotificationManager.Default.GetAllAsync();
            Console.WriteLine($"[PhoneHub] Own Windows notification queue contains submitted ID={ownNotices.Any(n => n.Id == id)}; count={ownNotices.Count}.");
        }
        catch (Exception error) { Console.WriteLine($"[PhoneHub] Own notification queue query HRESULT=0x{error.HResult:X8}."); }
    }
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && action == "phonehub") OpenRequested?.Invoke();
    }
    public async void Clear()
    {
        var id = _lastId; _lastId = 0;
        if (id == 0) return;
        try { await AppNotificationManager.Default.RemoveByIdAsync(id); }
        catch (Exception error) { Console.WriteLine($"[PhoneHub] Notification cleanup HRESULT=0x{error.HResult:X8}."); }
    }
    public void Dispose()
    {
        Clear();
        if (!_registered) return;
        AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
        AppNotificationManager.Default.Unregister();
        _registered = false;
    }
}

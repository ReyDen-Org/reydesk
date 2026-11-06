using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Platform;
using ReyDesk.Windows.Settings;

namespace ReyDesk.Windows;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>Shared services, accessed as a simple static holder rather than a DI
    /// container - reasonable at this app's current size (revisit if
    /// this ever needs to grow into something more formal).</summary>
    internal static AppSettingsService AppSettings { get; private set; } = null!;
    internal static DeviceIdentityService DeviceIdentity { get; private set; } = null!;
    internal static SessionCoordinator Session { get; private set; } = null!;

    internal static nint MainWindowHandle => ((App)Current)._window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
    private Window? _window;
    private TrayIcon? _trayIcon;
    private readonly PhoneHubWindowsAlerts _phoneAlerts = new();
    private bool _isQuitting;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Redirects Console.WriteLine (used throughout for diagnostics - pairing,
    /// WebRTC state, capture/encode failures) to a file, since this unpackaged
    /// WinExe-subsystem app has no console window to print to when launched normally -
    /// without this, every Console.WriteLine call in the codebase went nowhere anyone
    /// could see. Also logs otherwise-fatal unhandled exceptions before the process
    /// dies, so a real crash leaves a trace instead of just silently disappearing.</summary>
    private static void SetUpDiagnosticLogging()
    {
        var migrationNote = LegacyDataMigration.Run();
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReyDesk", "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // Keep the previous run's log: a crash is usually read after the app was relaunched.
        try { if (File.Exists(logPath) && new FileInfo(logPath).Length > 0) File.Copy(logPath, Path.Combine(Path.GetDirectoryName(logPath)!, "app.previous.log"), true); }
        catch (IOException) { }
        var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
        if (migrationNote is not null) Console.WriteLine(migrationNote);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.WriteLine($"[FATAL] Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // A fire-and-forget task faulted. Log it and mark it observed so it can never take the process down.
            Console.WriteLine($"[ERROR] Unobserved task exception (kept alive): {e.Exception}");
            e.SetObserved();
        };
    }

    internal void ShowMainWindow()
    {
        ClearPhoneHubBalloon();
        _window?.AppWindow.Show();
        _window?.Activate();
    }
    /// <summary>A remote pairing approval only shows inside the window, so a minimized or tray-hidden window is restored and
    /// brought to front when a new request becomes pending. Resolving or timing out changes nothing here.</summary>
    private void BringApprovalToFront()
    {
        if (_window is null || Session.Remote.Gate.Pending is null) return;
        _window.DispatcherQueue.PostSafe(() =>
        {
            if (_window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            ShowMainWindow();
            SetForegroundWindow(MainWindowHandle);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    internal void ShowPhoneHubBalloon() => _phoneAlerts.ShowPhoneNotice();
    internal bool ShowPhoneHubTestBalloon() => _phoneAlerts.ShowTest();
    internal string PhoneHubAlertStatus => _phoneAlerts.Status;
    internal void ClearPhoneHubBalloon() { _trayIcon?.ClearPhoneHubAlert(); _phoneAlerts.Clear(); }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        SetUpDiagnosticLogging();
        _phoneAlerts.Initialize();
        this.UnhandledException += (_, e) =>
        {
            // UI-thread exceptions (async void handlers, dialogs) are logged and survived; state lives in the services, not the view.
            Console.WriteLine($"[ERROR] XAML UnhandledException (kept alive): {e.Exception}\n{e.Message}");
            e.Handled = _window is not null; // startup failures (no window yet) still terminate
        };

        AppSettings = AppSettingsService.LoadOrCreate();
        DeviceIdentity = DeviceIdentityService.LoadOrCreate();
        Session = new SessionCoordinator(DeviceIdentity);
        Session.StartRemote();
        Session.Remote.Gate.PendingChanged += BringApprovalToFront;
        Strings.Current = AppSettings.Language;

        _window = new MainWindow();

        // Spec §20 describes tray-minimize as the norm for background services, but
        // this is a per-user setting (default: fully exit) rather than fixed either
        // way - see Settings > General > "Close button".
        _window.Closed += (_, closedArgs) =>
        {
            if (_isQuitting) return;
            if (AppSettings.CloseButtonBehavior == CloseButtonBehavior.ExitApp)
            {
                _isQuitting = true;
                _trayIcon?.Dispose();
                _phoneAlerts.Dispose();
                Session.Dispose();
                return;
            }
            closedArgs.Handled = true;
            _window.AppWindow.Hide();
        };

        _window.Activate();
        _phoneAlerts.OpenRequested += () => _window.DispatcherQueue.PostSafe(() => {
            ShowMainWindow();
            if (_window.Content is Microsoft.UI.Xaml.Controls.Grid root &&
                System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<Microsoft.UI.Xaml.Controls.Frame>(root.Children))?.Content is MainPage page)
                page.OpenPhoneHub();
        });

        _trayIcon = new TrayIcon("ReyDesk", Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        void UpdateTrayState()
        {
            var sessions = System.Linq.Enumerable.Select(Session.TrustedDevices.GetAll(), d => Session.TryGetSessionState(d.DeviceId)).ToList();
            var server = RemoteUiState.Current.Server;
            _trayIcon.SetState(
                sessions.Contains(SIPSorcery.Net.RTCPeerConnectionState.connected) ? TrayState.Online :
                sessions.Exists(x => x is SIPSorcery.Net.RTCPeerConnectionState.connecting or SIPSorcery.Net.RTCPeerConnectionState.@new) ||
                    server is RemoteServerState.Connecting or RemoteServerState.Registering ? TrayState.Connecting :
                server == RemoteServerState.Offline ? TrayState.Error : TrayState.Offline);
        }
        Session.SessionStateChanged += _ => _window.DispatcherQueue.PostSafe(UpdateTrayState);
        RemoteUiState.Current.Changed += () => _window.DispatcherQueue.PostSafe(UpdateTrayState);
        UpdateTrayState();
        _trayIcon.OpenRequested += () =>
        {
            ShowMainWindow();
        };
        _trayIcon.PhoneHubOpenRequested += () =>
        {
            ShowMainWindow();
            if (_window.Content is Microsoft.UI.Xaml.Controls.Grid grid &&
                System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<Microsoft.UI.Xaml.Controls.Frame>(grid.Children))?.Content is MainPage page)
                page.OpenPhoneHub();
        };
        _trayIcon.QuitRequested += () =>
        {
            _isQuitting = true;
            _trayIcon.Dispose();
            _phoneAlerts.Dispose();
            Session.Dispose();
            _window.Close();
            Environment.Exit(0);
        };
    }
}

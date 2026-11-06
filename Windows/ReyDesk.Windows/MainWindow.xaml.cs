using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Settings;
using System.Runtime.InteropServices;

namespace ReyDesk.Windows;

/// <summary>
/// The application window: custom 48 high title bar (system caption buttons kept, contract 2.18) and a Frame hosting
/// pages. Add UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // A compact, predictable opening footprint; don't reopen maximized or
        // consume an entire monitor just because the desktop is large.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = Math.Min((int)(1180 * scale), workArea.Width);
        var height = Math.Min((int)(740 * scale), workArea.Height);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));

        // Persian is RTL (spec §24); FlowDirection is a normal inheriting
        // property, so setting it here propagates live to the whole tree - unlike
        // the StaticResource-bound theme brushes, this doesn't need a restart.
        var flow = Strings.IsRtl(App.AppSettings.Language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RootFrame.FlowDirection = flow;
        TitleContent.FlowDirection = flow;

        // ReyDesk is dark only (contract section 1). The built-in Fluent controls (Button, ToggleSwitch,
        // ContentDialog, etc. from XamlControlsResources) follow ElementTheme, so force Dark here regardless of the OS theme.
        RootFrame.RequestedTheme = ElementTheme.Dark;

        RootGrid.Loaded += (_, _) => UpdateTitleBarMetrics();
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarMetrics();
        AppWindow.Changed += (_, _) => DispatcherQueue.PostSafe(UpdateTitleBarMetrics);

        // Navigate the root frame to the main page on startup (/rd-gallery opens the hidden control gallery).
        RootFrame.Navigate(Environment.GetCommandLineArgs().Contains("/rd-gallery") ? typeof(Controls.ControlGallery) : typeof(MainPage));
    }

    /// <summary>Caption buttons stay system (snap layouts); only their colours follow the tokens.</summary>
    private void StyleCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var clear = Microsoft.UI.Colors.Transparent;
        var fg = Controls.RDMotion.ColorOf("RDcFg1");
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = clear;
        bar.ButtonInactiveBackgroundColor = clear;
        bar.ButtonForegroundColor = fg;
        bar.ButtonHoverForegroundColor = fg;
        bar.ButtonPressedForegroundColor = fg;
        bar.ButtonHoverBackgroundColor = Controls.RDMotion.ColorOf("RDcInk4");
        bar.ButtonPressedBackgroundColor = Controls.RDMotion.ColorOf("RDcLine");
        bar.ButtonInactiveForegroundColor = Controls.RDMotion.ColorOf("RDcFgFaint");
    }

    /// <summary>Reserve the caption button insets (physical pixels to DIPs) and show the status pill at 1000 wide or more.</summary>
    private void UpdateTitleBarMetrics()
    {
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        if (!(scale > 0)) scale = 1.0;
        // Insets can be negative or not finite while the window is minimized or still being created; GridLength throws on those.
        static double Dips(double v) => double.IsFinite(v) && v > 0 ? v : 0;
        TitleLeftInset.Width = new GridLength(Dips(AppWindow.TitleBar.LeftInset / scale));
        TitleRightInset.Width = new GridLength(Dips(AppWindow.TitleBar.RightInset / scale));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

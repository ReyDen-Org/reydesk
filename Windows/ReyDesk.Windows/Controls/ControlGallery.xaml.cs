using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ReyDesk.Windows.Controls;

/// <summary>Hidden debug gallery, see the header of ControlGallery.xaml for how to reach it.</summary>
public sealed partial class ControlGallery : Page
{
    private static readonly string[] IconNames =
    {
        "Connection", "Devices", "PhoneHub", "Transfers", "Settings", "Copy", "Check", "Qr", "Globe", "Router", "ArrowLink", "Server", "Shield", "Code",
        "Update", "Pulse", "Key", "Power", "Trash", "Edit", "Info", "Close", "Chevron", "Plus", "Folder", "Bell", "Eye", "Search", "Warning", "Id",
        "PhoneCheck", "Menu",
    };

    private static bool _rtl;

    public ControlGallery()
    {
        InitializeComponent();
        ReducedToggle.IsOn = RDMotion.ForceReduced;
        RtlToggle.IsOn = _rtl;
        Root.FlowDirection = _rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        ListA.ItemsSource = new[] { "Quality", "Balanced", "Speed", "Auto", "Battery saver", "Low latency", "Ultra", "Custom", "Archive", "Review" };
        ListB.ItemsSource = new[] { "Alpha", "Beta", "Gamma" };
        ListB.SelectedIndex = 1;
        ListC.ItemsSource = new[] { "One" };

        foreach (var s in new[] { RDRingState.Idle, RDRingState.Connecting, RDRingState.ConnectedLocal, RDRingState.ConnectedRemote, RDRingState.Error })
            Rings.Items.Add(new RDStatusRing { State = s, Compact = true, StatusText = s.ToString(), Width = 200 });

        foreach (var n in IconNames)
        {
            var cell = new StackPanel { Width = 96, Spacing = 6, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
            cell.Children.Add(new RDIcon { Glyph = n, Size = 24, Foreground = RDMotion.BrushOf("RDTextBrush"), Directional = n is "Chevron" or "ArrowLink" or "Transfers" });
            cell.Children.Add(new TextBlock { Text = n, Style = (Style)Application.Current.Resources["RDTextHelper"] });
            IconGrid.Items.Add(cell);
        }
    }

    private void Reduced_Toggled(object sender, RoutedEventArgs e)
    {
        if (RDMotion.ForceReduced == ReducedToggle.IsOn) return;
        RDMotion.ForceReduced = ReducedToggle.IsOn;
        Frame?.Navigate(typeof(ControlGallery));
    }

    private void Rtl_Toggled(object sender, RoutedEventArgs e)
    {
        _rtl = RtlToggle.IsOn;
        Root.FlowDirection = _rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private void Toast_Info(object sender, RoutedEventArgs e) => Toast.Show(RDToastKind.Info, "Info toast message");
    private void Toast_Success(object sender, RoutedEventArgs e) => Toast.Show(RDToastKind.Success, "Copied to clipboard");
    private void Toast_Warning(object sender, RoutedEventArgs e) => Toast.Show(RDToastKind.Warning, "Phone went offline", "Retry", () => { });
    private void Toast_Error(object sender, RoutedEventArgs e) => Toast.Show(RDToastKind.Error, "Could not reach the server");
}

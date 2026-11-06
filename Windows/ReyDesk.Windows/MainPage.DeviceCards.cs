using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Devices;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Pairing;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private static string DeviceLabel(TrustedDeviceRecord device) =>
        string.IsNullOrWhiteSpace(device.Alias) ? DeviceArtwork.GetDisplayName(device.DisplayName) : device.Alias;

    private void RefreshSidebarFooter()
    {
        var assembly = typeof(App).Assembly;
        var version = ReyDesk.Windows.Platform.AppInfo.Version;
        SidebarVersionLabel.Text = Strings.Get("settings.version");
        SidebarVersion.Text = version;
        SidebarBuild.Text = (assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "") + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        SidebarCopyrightText.Text = $"© {N(DateTime.Now.Year)} ReyDen\n" + ConnectionStrings.Get("byReyDen");
        AutomationProperties.SetName(SidebarCopyright, string.Format(Strings.Get("settings.copyright"), N(DateTime.Now.Year)));
    }

    private TextBlock DeviceText(string text, int size = 12, bool strong = false) => new()
    {
        Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Foreground = Brush(strong ? "RDTextBrush" : "RDTextMutedBrush")
    };

    private Button DeviceAction(string key, string glyph, bool danger = false)
    {
        var button = new Button
        {
            Content = Ico(glyph, 18, danger ? "RDDangerBrush" : "RDTextMutedBrush"), Style = St(danger ? "RDIconDangerButton" : "RDIconButton"),
            Width = 42, Height = 42, MinHeight = 42, MinWidth = 42
        };
        SetAccessibleName(button, key);
        ToolTipService.SetToolTip(button, Strings.Get(key));
        return button;
    }

    private static string DeviceKeyLabel(TrustedDeviceRecord device)
    {
        try { return "KEY " + KeyId.Display(KeyId.FromSpki(Convert.FromBase64String(device.PublicKeyBase64))); }
        catch (FormatException) { return device.DeviceId[..Math.Min(8, device.DeviceId.Length)]; }
    }

    private static string PathKey(RDPathKind kind) => kind switch { RDPathKind.Lan => "pathLan", RDPathKind.Direct => "pathDirect", _ => "pathRelay" };

    /// <summary>Gallery card (2.8): artwork area, name, Key ID, status chip, path badge, unattended toggle, icon actions.</summary>
    private Border CreateDeviceRow(TrustedDeviceRecord device)
    {
        var state = App.Session.TryGetSessionState(device.DeviceId);
        var active = IsActive(state);
        var connected = state == SIPSorcery.Net.RTCPeerConnectionState.connected;
        var statusKey = connected ? "devices.sharing" : active ? "devices.connecting" : "devices.paired";
        var column = new StackPanel { Spacing = 12 };
        column.Children.Add(new Border
        {
            Height = 170, CornerRadius = new CornerRadius(12), Background = Brush("RDSurfaceBrush"), Padding = new Thickness(10),
            Child = CreateDeviceArtwork(device.DisplayName, device.DeviceId, 130)
        });
        var details = new StackPanel { Spacing = 4 };
        details.Children.Add(new TextBlock { Text = DeviceLabel(device), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(device.Alias)) details.Children.Add(DeviceText(DeviceArtwork.GetDisplayName(device.DisplayName)));
        details.Children.Add(Mono(DeviceKeyLabel(device)));
        column.Children.Add(details);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chips.Children.Add(Chip(Strings.Get(statusKey), connected ? "RDSuccessBrush" : active ? "RDWarningBrush" : "RDTextFaintBrush"));
        if (connected)
        {
            var kind = RemoteUiState.Current.Path ?? RDPathKind.Lan;
            chips.Children.Add(new RDPathBadge { Kind = kind, Text = ConnectionStrings.Get(PathKey(kind)), VerticalAlignment = VerticalAlignment.Center });
        }
        column.Children.Add(chips);
        column.Children.Add(DeviceText(string.Format(Strings.Get("devices.lastConnected"), FormatDeviceDate(device.LastSeenUnixMs))));
        column.Children.Add(CreateUnattendedControl(device));
        var actions = new Grid { ColumnSpacing = 6, BorderBrush = Brush("RDLineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = DeviceAction("devices.details", "Info");
        info.Click += async (_, _) => await ShowDeviceDetailsAsync(device);
        actions.Children.Add(info);
        var rename = DeviceAction("devices.rename", "Edit");
        rename.Click += async (_, _) => await RenameDeviceAsync(device);
        Grid.SetColumn(rename, 1); actions.Children.Add(rename);
        if (active)
        {
            var disconnect = DeviceAction("devices.disconnect", "Power");
            disconnect.Click += (_, _) => App.Session.DisconnectDevice(device.DeviceId);
            Grid.SetColumn(disconnect, 2); disconnect.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(disconnect);
        }
        var forget = DeviceAction("devices.forget", "Trash", danger: true);
        forget.Click += async (_, _) => await ForgetDeviceAsync(device);
        Grid.SetColumn(forget, 3); actions.Children.Add(forget);
        column.Children.Add(actions);
        return CardBox(column, new Thickness(12));
    }

    private static string FormatDeviceDate(long value) => value > 0 ? FormatLocalDateTime(DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime()) : Dash;

    private ContentDialog DeviceDialog(string title) => Dlg(title, "", null, Strings.Get("common.close"));

    private async Task ShowDeviceDetailsAsync(TrustedDeviceRecord device)
    {
        var body = new StackPanel { Spacing = 14, MinWidth = 260 };
        void Detail(string key, string value)
        {
            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(DeviceText(Strings.Get(key)));
            var text = DeviceText(value, 14, true); text.IsTextSelectionEnabled = true; if (key == "devices.identifier") { text.FontFamily = (FontFamily)Application.Current.Resources["RDFontMono"]; text.FlowDirection = FlowDirection.LeftToRight; }
            group.Children.Add(text); body.Children.Add(group);
        }
        Detail("devices.name", DeviceLabel(device));
        Detail("devices.model", DeviceArtwork.GetDisplayName(device.DisplayName) + " (" + device.DisplayName + ")");
        var state = App.Session.TryGetSessionState(device.DeviceId);
        Detail("connection.status", Strings.Get(state == SIPSorcery.Net.RTCPeerConnectionState.connected ? "devices.sharing" : IsActive(state) ? "devices.connecting" : "devices.paired"));
        Detail("devices.pairedDate", FormatDeviceDate(device.PairedAtUnixMs));
        Detail("devices.lastSeen", FormatDeviceDate(device.LastSeenUnixMs));
        Detail("devices.identifier", device.DeviceId);
        var copy = new Button { Content = Strings.Get("devices.copyId"), Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => { CopyText(device.DeviceId); ShowToast(RDToastKind.Success, Strings.Get("devices.copied")); };
        body.Children.Add(copy);
        var dialog = DeviceDialog(Strings.Get("devices.details"));
        dialog.Content = new ScrollViewer { MaxHeight = 420, Content = body };
        await dialog.ShowAsync();
    }

    private async Task RenameDeviceAsync(TrustedDeviceRecord device)
    {
        var input = new TextBox { Text = device.Alias ?? "", PlaceholderText = DeviceArtwork.GetDisplayName(device.DisplayName), MaxLength = 60, MinWidth = 260, Style = St("RDTextBox") };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Txt(Strings.Get("devices.name"), "RDTextLabel")); body.Children.Add(input); body.Children.Add(Txt(Strings.Get("devices.renameHint"), "RDTextHelper"));
        var dialog = DeviceDialog(Strings.Get("devices.rename"));
        dialog.Content = body; dialog.PrimaryButtonText = Strings.Get("common.save"); dialog.PrimaryButtonStyle = St("RDPrimaryButton"); dialog.CloseButtonText = Strings.Get("common.cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { App.Session.TrustedDevices.Rename(device.DeviceId, input.Text); }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrustedDevices] Rename failed: {ex}");
            ShowToast(RDToastKind.Error, Strings.Get("devices.renameFailed") + ": " + ex.Message);
        }
    }
}

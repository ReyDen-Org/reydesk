using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Pairing;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private StackPanel CreateUnattendedControl(TrustedDeviceRecord device)
    {
        var panel = new StackPanel { Spacing = 6 };
        var toggle = NewToggle(device.UnattendedAllowed);
        AutomationProperties.SetName(toggle, Strings.Get("unattended.allow") + " · " + DeviceLabel(device));
        var row = new Grid { ColumnSpacing = 10, MinHeight = 36 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = Strings.Get("unattended.allow"), FontSize = 13, Foreground = Brush("RDTextMutedBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        var error = Txt("", "RDTextError");
        // Collapsed while empty so the card has no dead gap between the toggle and the divider.
        error.Visibility = Visibility.Collapsed;
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = error.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
        panel.Children.Add(row); panel.Children.Add(error);
        var changing = false;
        toggle.Toggled += async (_, _) => {
            if (changing) return;
            changing = true; toggle.IsEnabled = false; error.Text = "";
            var allowed = toggle.IsOn;
            try
            {
                var expectedKey = Convert.FromBase64String(device.PublicKeyBase64);
                if (allowed)
                {
                    await _consentDialog.WaitAsync();
                    try
                    {
                        var content = new StackPanel { Spacing = 12 };
                        content.Children.Add(new TextBlock { Text = DeviceLabel(device), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
                        content.Children.Add(Txt(Strings.Get("unattended.explanation"), "RDTextMuted"));
                        var fingerprint = new StackPanel { Spacing = 4 };
                        fingerprint.Children.Add(Txt(Strings.Get("unattended.fingerprint"), "RDTextLabel"));
                        fingerprint.Children.Add(Mono(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedKey))[..24]));
                        content.Children.Add(fingerprint);
                        var dialog = Dlg(Strings.Get("unattended.confirm"), content, Strings.Get("unattended.enable"), Strings.Get("common.cancel"), danger: true);
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { toggle.IsOn = false; return; }
                    }
                    finally { _consentDialog.Release(); }
                }
                // The stored key must still match; replacing/re-pairing a record revokes this approval.
                App.Session.TrustedDevices.SetUnattendedAllowed(device.DeviceId, expectedKey, allowed);
            }
            catch (Exception)
            {
                toggle.IsOn = device.UnattendedAllowed;
                error.Text = Strings.Get("unattended.failed");
            }
            finally { toggle.IsEnabled = true; changing = false; }
        };
        return panel;
    }
}

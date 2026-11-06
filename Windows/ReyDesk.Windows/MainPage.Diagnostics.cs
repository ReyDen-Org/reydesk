using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Pairing;
using System.Net.NetworkInformation;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private void AddDiagnosticsSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        var rows = new StackPanel { Spacing = 6 };
        var skeleton = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        for (var i = 0; i < 3; i++) skeleton.Children.Add(new RDSkeleton { Height = 14, Width = 200 + i * 40, Radius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left });
        var report = "";
        var refresh = new Button { Content = Strings.Get("diagnostics.refresh"), Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        var copy = new Button { Content = Strings.Get("diagnostics.copyReport"), Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };

        async Task RefreshAsync()
        {
            refresh.IsEnabled = copy.IsEnabled = false;
            rows.Visibility = Visibility.Collapsed; skeleton.Visibility = Visibility.Visible;
            rows.Children.Clear();
            var lines = new List<string>();
            try
            {
                var address = string.Format(Strings.Get("diagnostics.address"), App.Session.LocalEndpoint);
                var listening = await Task.Run(() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == PairingOfferFactory.LocalPairingPort));
                var listener = string.Format(Strings.Get("diagnostics.listener"), PairingOfferFactory.LocalPairingPort,
                    Strings.Get(listening ? "diagnostics.listening" : "diagnostics.notListening"));
                var checkedAt = string.Format(Strings.Get("diagnostics.checked"), FormatLocalDateTime(DateTimeOffset.Now));
                rows.Children.Add(StatusRow("", address, "RDSuccessBrush"));
                rows.Children.Add(StatusRow("", listener, listening ? "RDSuccessBrush" : "RDWarningBrush"));
                rows.Children.Add(StatusRow("", checkedAt, "RDTextFaintBrush"));
                lines.Add(address); lines.Add(listener); lines.Add(checkedAt);
                AddInternetDiagnostics(rows, lines);
            }
            catch (NetworkInformationException)
            {
                var message = Strings.Get("diagnostics.unavailable");
                rows.Children.Add(StatusRow("", message, "RDDangerBrush"));
                lines.Add(message);
            }
            report = string.Join(Environment.NewLine, lines);
            skeleton.Visibility = Visibility.Collapsed; rows.Visibility = Visibility.Visible;
            refresh.IsEnabled = copy.IsEnabled = true;
        }

        refresh.Click += async (_, _) => await RefreshAsync();
        copy.Click += (_, _) => { CopyText(report); ShowToast(RDToastKind.Success, ConnectionStrings.Get("copied")); };
        panel.Children.Add(rows); panel.Children.Add(skeleton);
        panel.Children.Add(Txt(Strings.Get("diagnostics.discovery"), "RDTextHelper"));
        panel.Children.Add(Txt(Strings.Get("diagnostics.security"), "RDTextHelper"));
        panel.Children.Add(Txt(Strings.Get("diagnostics.internet"), "RDTextHelper"));
        panel.Children.Add(Txt(Strings.Get("diagnostics.vpn"), "RDTextHelper"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(refresh); buttons.Children.Add(copy);
        panel.Children.Add(buttons);
        AddCard("diagnostics.title", "Pulse", panel);
        _ = RefreshAsync();
    }

    /// <summary>RemoteService support lines (server, IDs, flags, ICE counts) plus the current path chip.</summary>
    private void AddInternetDiagnostics(StackPanel rows, List<string> lines)
    {
        var remote = App.Session.Remote;
        var (_, brush) = RemoteUiState.Current.PillState();
        foreach (var line in remote.DiagnosticsText().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            rows.Children.Add(StatusRow("", line, brush));
            lines.Add(line);
        }
        if (remote.State.Path is { } path)
            rows.Children.Add(new Controls.RDPathBadge { Kind = path, Text = PathText(path), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 2, 0, 0) });
    }
}

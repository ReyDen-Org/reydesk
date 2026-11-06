using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Updates;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private void AddUpdateSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        versionRow.Children.Add(Txt(Strings.Get("updates.installed"), "RDTextLabel"));
        versionRow.Children.Add(Mono(UpdateChecker.DisplayVersion));
        panel.Children.Add(versionRow);
        var status = Txt(Strings.Get("updates.ready"), "RDTextMuted");
        AutomationPropertiesForUpdate(status);
        panel.Children.Add(status);
        var checkedAt = Txt("", "RDTextHelper"); checkedAt.Visibility = Visibility.Collapsed; panel.Children.Add(checkedAt);
        var release = new HyperlinkButton { Content = Strings.Get("updates.openRelease"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        panel.Children.Add(release);
        var notes = Txt("", "RDTextMuted"); notes.MaxHeight = 160; notes.IsTextSelectionEnabled = true;
        var notesScroll = new ScrollViewer { Content = notes, MaxHeight = 160, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(notesScroll);
        var progress = new RDProgress { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);
        var check = new Button { Content = Strings.Get("updates.check"), Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(check);
        check.Click += async (_, _) =>
        {
            release.Visibility = notesScroll.Visibility = checkedAt.Visibility = Visibility.Collapsed;
            notes.Text = "";
            check.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            release.Visibility = notesScroll.Visibility = Visibility.Collapsed; notes.Text = "";
            status.Text = Strings.Get("updates.checking");
            var result = await new UpdateChecker().CheckAsync(Strings.Current == ReyDesk.Windows.Settings.AppLanguage.Persian ? "fa" : "en");
            status.Text = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => string.Format(Strings.Get("updates.available"), result.LatestVersion),
                UpdateCheckStatus.UpToDate => Strings.Get("updates.current"),
                UpdateCheckStatus.SourceUnavailable => Strings.Get("updates.unavailable"),
                UpdateCheckStatus.RateLimited => Strings.Get("updates.rateLimited"),
                UpdateCheckStatus.AccessDenied => Strings.Get("updates.denied"),
                UpdateCheckStatus.TimedOut => Strings.Get("updates.timeout"),
                UpdateCheckStatus.NetworkError => Strings.Get("updates.networkError"),
                UpdateCheckStatus.InvalidRelease => Strings.Get("updates.invalidRelease"),
                _ => Strings.Get("updates.failed")
            };
            checkedAt.Text = Strings.Get("updates.checkedAt") + " " + FormatLocalDateTime(DateTimeOffset.Now);
            checkedAt.Visibility = Visibility.Visible;
            if (result.ReleaseUri is { } uri)
            {
                release.NavigateUri = uri; release.Visibility = Visibility.Visible;
                notes.Text = result.Notes ?? "";
                notesScroll.Visibility = string.IsNullOrWhiteSpace(notes.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            progress.Visibility = Visibility.Collapsed;
            check.IsEnabled = true;
        };
        AddCard("updates.title", "Update", panel);
    }

    private static void AutomationPropertiesForUpdate(TextBlock status) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
}

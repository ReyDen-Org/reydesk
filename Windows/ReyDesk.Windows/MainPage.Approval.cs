using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Remote;
using ReyDesk.Windows.Controls;

namespace ReyDesk.Windows;

/// <summary>T11: the on-PC approval dialog (peer, Key ID, SAS, allow-remote checkbox, countdown). The owner's click is the only
/// way a new phone is trusted; closing, Esc, timeout and Decline all decline.</summary>
public sealed partial class MainPage
{
    private PendingApproval? _approvalShown;
    private ContentDialog? _approvalDialog;

    private void CheckApproval()
    {
        var pending = RemoteSvc.Gate.Pending;
        if (pending is null)
        {
            if (_approvalShown is { IsResolved: true }) _approvalDialog?.Hide();
            return;
        }
        if (_approvalShown is not null)
        {
            if (!ReferenceEquals(pending, _approvalShown)) _approvalDialog?.Hide(); // replaced: the old one is already declined
            return;
        }
        _ = ShowApprovalAsync(pending);
    }

    private async Task ShowApprovalAsync(PendingApproval pending)
    {
        _approvalShown = pending;
        var request = pending.Request;
        var countdown = Txt("", "RDTextMuted");
        var allow = request.Medium == ConnectionMedium.Local && !request.AlreadyTrusted && !request.CompareOnly
            ? new CheckBox { Style = St("RDCheckBox"), IsChecked = request.AllowRemoteDefault, Content = new TextBlock { Text = ConnectionStrings.Get("apprAllowRemote"), TextWrapping = TextWrapping.Wrap } }
            : null;
        // CompareOnly: the SAS is shown while the phone compares it; no Approve button (the approval dialog follows the phone's confirm).
        var dialog = request.CompareOnly
            ? Dlg(ConnectionStrings.Get("cmpTitle"), BuildApprovalBody(request, countdown, null), null, ConnectionStrings.Get("cmpCancel"))
            : Dlg(ConnectionStrings.Get(request.AlreadyTrusted ? "apprTitleTrusted" : "apprTitleNew"), BuildApprovalBody(request, countdown, allow),
                ConnectionStrings.Get("apprApprove"), ConnectionStrings.Get("apprDecline"));
        _approvalDialog = dialog;

        void Tick()
        {
            var left = pending.ExpiresAt - DateTimeOffset.UtcNow;
            if (pending.IsResolved || left <= TimeSpan.Zero) { dialog.Hide(); return; }
            countdown.Text = string.Format(ConnectionStrings.Get("apprExpires"), N($"{(int)left.TotalMinutes}:{left.Seconds:D2}"));
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        Tick();
        timer.Start();

        var result = ContentDialogResult.None;
        try
        {
            // One ContentDialog per window: if another dialog is open, wait for it and try again until this request is over.
            while (!pending.IsResolved)
            {
                try { result = await dialog.ShowAsync(); break; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    await Task.Delay(400);
                }
            }
        }
        finally
        {
            timer.Stop();
            pending.Resolve(result == ContentDialogResult.Primary, allow?.IsChecked == true);
            _approvalDialog = null;
            _approvalShown = null;
        }
        CheckApproval();
    }

    private StackPanel BuildApprovalBody(PairingApprovalRequest request, TextBlock countdown, CheckBox? allow)
    {
        var body = new StackPanel { Spacing = 14, MinWidth = 340 };

        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(IconTile("PhoneCheck", 56, 24));
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = request.Peer.DisplayName;
        if (name.Length > 40) name = name[..40];
        who.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(name) ? ConnectionStrings.Get("apprPhone") : name, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(new RDPathBadge { Kind = request.Medium == ConnectionMedium.Remote ? RDPathKind.Direct : RDPathKind.Lan, Text = ConnectionStrings.Get(request.Medium == ConnectionMedium.Remote ? "apprInternet" : "apprLan"), HorizontalAlignment = HorizontalAlignment.Left });
        Grid.SetColumn(who, 1); head.Children.Add(who);
        body.Children.Add(head);

        body.Children.Add(Mono("KEY " + KeyId.Display(request.PeerKeyId)));

        if (!string.IsNullOrEmpty(request.Sas))
        {
            body.Children.Add(Txt(ConnectionStrings.Get(request.CompareOnly ? "cmpHint" : "apprCompare"), "RDTextMuted"));
            body.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16), Background = Brush("RDIconTileBrush"), BorderBrush = Brush("RDIconTileLineBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12),
                Child = new TextBlock
                {
                    Text = request.Sas, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["RDFontMono"], FontSize = (double)Application.Current.Resources["RDTypeSas"],
                    FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Center, IsTextSelectionEnabled = false,
                }
            });
        }
        if (allow is not null) body.Children.Add(allow);
        body.Children.Add(countdown);
        return body;
    }

#if DEBUG
    /// <summary>Debug builds only. Environment REYDESK_DEBUG_PAGE=connection|devices|settings opens that page, and
    /// REYDESK_DEBUG_APPROVAL=1 shows a fake approval dialog (no gate, nothing is trusted). Never compiled into Release.</summary>
    private async void StartDebugHooks()
    {
        var page = Environment.GetEnvironmentVariable("REYDESK_DEBUG_PAGE");
        if (!string.IsNullOrEmpty(page)) Navigate(page);
        // REYDESK_DEBUG_ID=connecting|offline|online fakes the Desk ID card state (UI only, nothing is sent or stored).
        var idState = Environment.GetEnvironmentVariable("REYDESK_DEBUG_ID");
        if (!string.IsNullOrEmpty(idState))
        {
            await Task.Delay(1500);
            RemoteUiState.Current.Update(e =>
            {
                e.RemoteAllowed = true;
                e.Server = idState switch { "online" => RemoteServerState.Online, "offline" => RemoteServerState.Offline, _ => RemoteServerState.Registering };
                e.ServerDetail = idState == "offline" ? "connection refused" : null;
                e.DeskId = idState == "online" ? "7KQ4MZ2PX9DF" : null;
                e.KeyId = "KEHT-88PC-7B9D";
            });
        }
        if (Environment.GetEnvironmentVariable("REYDESK_DEBUG_APPROVAL") != "1") return;
        await Task.Delay(900);
        var request = new PairingApprovalRequest(PairingMethod.LanSas, new DeviceIdentity { DeviceId = "debug", DisplayName = "Pixel 8" }, "4F7Q2M9XHC3D", "482 913",
            ConnectionMedium.Local, false, false, TimeSpan.FromMinutes(5));
        await ShowApprovalAsync(new PendingApproval(request));
    }
#endif
}

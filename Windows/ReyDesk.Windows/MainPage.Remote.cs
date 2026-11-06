using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Remote;

namespace ReyDesk.Windows;

/// <summary>T11: the bridge between RemoteService and the UI (state, requests, approval gate) and the Settings "Internet connection" card.</summary>
public sealed partial class MainPage
{
    private bool _remoteAttached;
    private bool _generatingCode;
    private bool _codeWanted;
    private bool _hadCode;
    private string _lastOfferKey = "";

    private bool _syncingInternet;
    private ToggleSwitch _inetAllow = null!;
    private ToggleSwitch _inetRelay = null!;
    private Ellipse _inetDot = null!;
    private TextBlock _inetState = null!;
    private TextBlock _inetDetail = null!;
    private TextBlock _inetDeskId = null!;
    private RDPathBadge _inetPath = null!;
    private Button _inetRetry = null!;
    private TextBox _inetUrl = null!;
    private TextBlock _inetUrlError = null!;
    private TextBlock _inetHost = null!;
    private TextBlock _inetPrivacyText = null!;

    private static RemoteService RemoteSvc => App.Session.Remote;

    private void InitializeRemoteBridge()
    {
        _lastOfferKey = OfferKey(RemoteSvc.State);
        Loaded += (_, _) => AttachRemote();
        Unloaded += (_, _) => DetachRemote();
    }

    private static string OfferKey(RemoteState s) => $"{s.RemoteAllowed}|{s.RendezvousUrl}|{s.DeskId}";

    private void AttachRemote()
    {
        if (_remoteAttached) return;
        _remoteAttached = true;
        var ui = RemoteUiState.Current;
        RemoteSvc.Changed += OnServiceChanged;
        RemoteSvc.Gate.PendingChanged += OnGatePendingChanged;
        ui.RetryRequested += OnRetryRequested;
        ui.RegenerateCodeRequested += OnRegenerateRequested;
        ui.CancelCodeRequested += OnCancelCodeRequested;
        RemoteSvc.Gate.UiAttached = true;
        SyncRemoteState();
        CheckApproval();
#if DEBUG
        StartDebugHooks();
#endif
    }

    private void DetachRemote()
    {
        if (!_remoteAttached) return;
        _remoteAttached = false;
        var ui = RemoteUiState.Current;
        RemoteSvc.Gate.UiAttached = false;
        RemoteSvc.Changed -= OnServiceChanged;
        RemoteSvc.Gate.PendingChanged -= OnGatePendingChanged;
        ui.RetryRequested -= OnRetryRequested;
        ui.RegenerateCodeRequested -= OnRegenerateRequested;
        ui.CancelCodeRequested -= OnCancelCodeRequested;
    }

    // Service events arrive on background threads.
    private void OnServiceChanged() => DispatcherQueue.PostSafe(SyncRemoteState);
    private void OnGatePendingChanged() => DispatcherQueue.PostSafe(CheckApproval);
    private void OnRetryRequested() => RemoteSvc.Retry();
    private void OnCancelCodeRequested() { _codeWanted = false; RemoteSvc.CancelRemoteCode(); }
    private void OnRegenerateRequested() => DispatcherQueue.PostSafe(() => _ = GenerateCodeAsync());

    private void SyncRemoteState()
    {
        var s = RemoteSvc.State;
        RemoteUiState.Current.Update(e =>
        {
            e.DeskId = s.DeskId; e.KeyId = s.KeyId; e.Server = s.Server; e.ServerDetail = s.ServerDetail; e.Path = s.Path;
            e.RemoteAllowed = s.RemoteAllowed; e.RemoteCode = s.RemoteCode; e.CodeExpiresAt = s.CodeExpiresAt;
        });

        // The QR carries the Desk ID and the server URL, so it is rebuilt when either (or the remote toggle) changes.
        var key = OfferKey(s);
        if (key != _lastOfferKey) { _lastOfferKey = key; _ = RefreshPairingOfferAsync(); }

        var hasCode = !string.IsNullOrEmpty(s.RemoteCode);
        if (_hadCode && !hasCode) _codeWanted = false; // expired, used or cancelled: do not silently issue another
        _hadCode = hasCode;
        var online = s.RemoteAllowed && s.Server == RemoteServerState.Online;
        if (_codePanelOpen && _codeWanted && online && !hasCode && !_generatingCode) _ = GenerateCodeAsync();
    }

    /// <summary>"Add phone over internet" / Regenerate: arms a fresh 6 digit code on the service.</summary>
    private async Task GenerateCodeAsync()
    {
        if (_generatingCode) return;
        _codeWanted = true;
        var s = RemoteSvc.State;
        if (!s.RemoteAllowed || s.Server != RemoteServerState.Online) { RefreshCodePanel(); return; }
        _generatingCode = true;
        RefreshCodePanel();
        var ok = false;
        try { ok = await RemoteSvc.GenerateRemoteCodeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Remote] code request failed: {ex.GetType().Name}"); }
        _generatingCode = false;
        if (!ok) { _codeWanted = false; ShowToast(RDToastKind.Error, ConnectionStrings.Get("codeFailed")); }
        RefreshCodePanel();
    }

    // ---------------- Settings: Internet connection ----------------

    private void AddInternetSection()
    {
        var panel = new StackPanel { Spacing = 14 };

        _inetAllow = NewToggle(false);
        _inetAllow.Toggled += (_, _) => { if (!_syncingInternet) RemoteSvc.SetRemoteAllowed(_inetAllow.IsOn); };
        panel.Children.Add(ToggleRow("Globe", Strings.Get("settings.internetAllow"), Strings.Get("settings.internetHelp"), _inetAllow));

        var server = new Grid { ColumnSpacing = 10, MinHeight = 36 };
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _inetDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        server.Children.Add(_inetDot);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Txt(Strings.Get("settings.internetServer"), "RDTextLabel"));
        _inetState = new TextBlock { FontSize = 13, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap };
        _inetDetail = Txt("", "RDTextHelper");
        _inetDeskId = new TextBlock { FontSize = 13, Foreground = Brush("RDTextBrush"), FontFamily = (FontFamily)Application.Current.Resources["RDFontMono"], FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        text.Children.Add(_inetState); text.Children.Add(_inetDetail); text.Children.Add(_inetDeskId);
        Grid.SetColumn(text, 1); server.Children.Add(text);
        _inetPath = new RDPathBadge { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        Grid.SetColumn(_inetPath, 2); server.Children.Add(_inetPath);
        _inetRetry = new Button { Content = ConnectionStrings.Get("retry"), Style = St("RDGhostButton"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _inetRetry.Click += (_, _) => RemoteUiState.Current.RequestRetry();
        Grid.SetColumn(_inetRetry, 3); server.Children.Add(_inetRetry);
        panel.Children.Add(server);

        var advanced = new StackPanel { Spacing = 8 };
        var advancedBox = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var advancedToggle = new Button { Content = ConnectionStrings.Get("inetAdvanced"), Style = St("RDGhostButton"), HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(advancedToggle, ConnectionStrings.Get("inetAdvanced"));
        advancedToggle.Click += (_, _) => advancedBox.Visibility = advancedBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        advanced.Children.Add(advancedToggle);
        advanced.Children.Add(advancedBox);
        var urlBox = advancedBox;
        _inetHost = new TextBlock { FontSize = 12, Foreground = Brush("RDTextFaintBrush"), FontFamily = (FontFamily)Application.Current.Resources["RDFontMono"], FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap };
        urlBox.Children.Add(Txt(ConnectionStrings.Get("inetConnectsTo"), "RDTextLabel"));
        urlBox.Children.Add(_inetHost);
        urlBox.Children.Add(Txt(ConnectionStrings.Get("inetServerUrl"), "RDTextLabel"));
        var urlRow = new Grid { ColumnSpacing = 8 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _inetUrl = new TextBox
        {
            Style = St("RDTextBox"), FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left,
            PlaceholderText = ConnectionStrings.Get("inetUrlHint"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_inetUrl, ConnectionStrings.Get("inetServerUrl"));
        _inetUrl.KeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Enter) { CommitServerUrl(); e.Handled = true; } };
        _inetUrl.TextChanged += (_, _) => { _inetUrl.FontFamily = _inetUrl.Text.Length > 0 ? (FontFamily)Application.Current.Resources["RDFontMono"] : (FontFamily)Application.Current.Resources["RDFontUi"]; if (_inetUrlError.Visibility == Visibility.Visible) SetUrlError(false); };
        urlRow.Children.Add(_inetUrl);
        var apply = new Button { Content = ConnectionStrings.Get("inetApply"), Style = St("RDSecondaryButton"), VerticalAlignment = VerticalAlignment.Top };
        apply.Click += (_, _) => CommitServerUrl();
        Grid.SetColumn(apply, 1); urlRow.Children.Add(apply);
        var reset = new Button { Content = ConnectionStrings.Get("inetReset"), Style = St("RDGhostButton"), VerticalAlignment = VerticalAlignment.Top };
        reset.Click += (_, _) => { _inetUrl.Text = ""; CommitServerUrl(); };
        Grid.SetColumn(reset, 2); urlRow.Children.Add(reset);
        urlBox.Children.Add(urlRow);
        _inetUrlError = new TextBlock { Text = ConnectionStrings.Get("inetUrlInvalid"), FontSize = 12, Foreground = Brush("RDDangerBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        urlBox.Children.Add(_inetUrlError);

        _inetRelay = NewToggle(false);
        _inetRelay.Toggled += (_, _) => { if (!_syncingInternet) RemoteSvc.SetRelayAllowed(_inetRelay.IsOn); };
        panel.Children.Add(ToggleRow("Server", ConnectionStrings.Get("inetRelay"), ConnectionStrings.Get("inetRelayHelp"), _inetRelay));

        var privacy = new Grid { ColumnSpacing = 10 };
        privacy.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        privacy.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shield = Ico("Shield", 16, "RDTextFaintBrush"); shield.VerticalAlignment = VerticalAlignment.Top; shield.Margin = new Thickness(0, 2, 0, 0);
        privacy.Children.Add(shield);
        _inetPrivacyText = Txt("", "RDTextHelper");
        Grid.SetColumn(_inetPrivacyText, 1); privacy.Children.Add(_inetPrivacyText);
        panel.Children.Add(privacy);
        panel.Children.Add(advanced);

        AddCard("settings.internet", "Router", panel);
        RefreshInternetSection();
    }

    private void SetUrlError(bool on)
    {
        _inetUrlError.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _inetUrl.Style = St(on ? "RDTextBoxError" : "RDTextBox");
    }

    private void CommitServerUrl()
    {
        try
        {
            RemoteSvc.SetRendezvousUrl(_inetUrl.Text.Trim());
            SetUrlError(false);
            ShowToast(RDToastKind.Success, ConnectionStrings.Get("inetSaved"));
        }
        catch (ArgumentException)
        {
            SetUrlError(true);
        }
    }

    private void RefreshInternetSection()
    {
        if (_inetAllow is null) return;
        var s = RemoteSvc.State;
        _syncingInternet = true;
        _inetAllow.IsOn = s.RemoteAllowed;
        _inetRelay.IsOn = s.RelayAllowed;
        var overrideUrl = App.AppSettings.RendezvousUrlOverride;
        if (_inetUrl.FocusState == FocusState.Unfocused && _inetUrl.Text != overrideUrl) _inetUrl.Text = overrideUrl;
        _syncingInternet = false;

        var host = Uri.TryCreate(s.RendezvousUrl, UriKind.Absolute, out var hostUri) ? hostUri.Host : s.RendezvousUrl;
        _inetHost.Text = host;
        _inetPrivacyText.Text = string.Format(ConnectionStrings.Get("inetPrivacyHost"), host);

        var (text, brush) = RemoteUiState.Current.PillState();
        if (s.RemoteAllowed && s.Server == RemoteServerState.Offline) text = ConnectionStrings.Get("srvUnreachable");
        _inetDot.Fill = Brush(brush);
        _inetState.Text = text;
        var detail = s.RemoteAllowed && s.Server == RemoteServerState.Offline ? s.ServerDetail : null;
        _inetDetail.Text = detail ?? "";
        _inetDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        var hasDeskId = !string.IsNullOrWhiteSpace(s.DeskId);
        _inetDeskId.Visibility = hasDeskId ? Visibility.Visible : Visibility.Collapsed;
        if (hasDeskId) _inetDeskId.Text = ConnectionStrings.Get("deskIdLabel") + "  " + ReyDesk.Windows.Identity.DeskId.Display(s.DeskId!);
        _inetRetry.Visibility = s.RemoteAllowed && s.Server == RemoteServerState.Offline ? Visibility.Visible : Visibility.Collapsed;
        if (s.RemoteAllowed && s.Server == RemoteServerState.Online && s.Path is { } path)
        {
            _inetPath.Kind = path; _inetPath.Text = PathText(path); _inetPath.Visibility = Visibility.Visible;
        }
        else _inetPath.Visibility = Visibility.Collapsed;
    }
}

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.PhoneHub;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using System.Threading;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private readonly Dictionary<string, PhoneInbox> _phoneInboxes = new();
    private readonly Dictionary<string, string> _phoneGenerations = new();
    private readonly Dictionary<string, TaskCompletionSource<CompanionResult>> _hubPending = new();
    private readonly SemaphoreSlim _consentDialog = new(1, 1);
    private string? _hubDevice;
    private string? _hubSelected;
    private bool _hubRefreshing;
    private bool _hubAttached;
    private sealed record HubChoice(string Tag, string Label) { public override string ToString() => Label; }
    private RDListBox _hubPhones = null!;
    private RDListBox _hubApps = null!;
    private TextBox _hubSearch = null!;
    private TextBlock _hubTitle = null!;
    private TextBlock _hubSubtitle = null!;
    private TextBlock _hubOverview = null!;
    private TextBlock _hubStatus = null!;
    private Border _hubChipHost = null!;
    private bool _hubAlertShown;
    private ToggleSwitch _hubPreviews = null!;
    private ToggleSwitch _hubAlerts = null!;
    private ToggleSwitch _hubShellAlerts = null!;
    private readonly Dictionary<string, PhoneAlertGate> _hubShellGates = new();
    private ToggleSwitch _hubFocus = null!;
    private Grid _hubRowPreviews = null!, _hubRowAlerts = null!, _hubRowShell = null!, _hubRowFocus = null!;
    private TextBlock _hubFeaturesTitle = null!, _hubNotifTitle = null!, _hubPrivacyTitle = null!;
    private Button _hubSync = null!;
    private Button _hubPause = null!;
    private Button _hubTestAlert = null!;
    private TextBlock _hubTestFeedback = null!;
    private Grid _hubColumns = null!;
    private StackPanel _hubLeft = null!;
    private StackPanel _hubRight = null!;
    private StackPanel _hubCards = null!;
    private StackPanel _hubDetail = null!;
    private TextBlock _hubPrivacy = null!;
    private TextBlock _hubLimits = null!;

    private static void SetRowText(Grid row, string label, string helper)
    {
        var text = (StackPanel)row.Children[1];
        ((TextBlock)text.Children[0]).Text = label;
        ((TextBlock)text.Children[1]).Text = helper;
        AutomationProperties.SetName((ToggleSwitch)row.Children[2], label);
    }

    private void InitializePhoneHub()
    {
        _hubTitle = new TextBlock { Style = St("RDTextTitle") };
        _hubSubtitle = Txt("", "RDTextMuted");
        _hubOverview = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap, TextReadingOrder = TextReadingOrder.DetectFromContent };
        _hubStatus = Txt("", "RDTextHelper");
        _hubChipHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        _hubPhones = new RDListBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _hubPhones.SelectionChanged += (_, _) => {
            if (_hubRefreshing) return;
            _hubDevice = (_hubPhones.SelectedItem as HubChoice)?.Tag;
            _hubSelected = null; ClearHubAlert(); RefreshHubApps(); RenderPhoneInbox();
        };
        _hubApps = new RDListBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 130 };
        _hubApps.SelectionChanged += (_, _) => { if (!_hubRefreshing) RenderPhoneInbox(); };
        _hubSearch = new TextBox { Style = St("RDTextBox"), MaxLength = 256, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(38, 8, 12, 8) };
        _hubSearch.TextChanged += (_, _) => RenderPhoneInbox();
        _hubSync = new Button { Style = St("RDSecondaryButton") };
        _hubSync.Click += async (_, _) => await SendHubAsync("phone_sync");
        _hubPause = new Button { Style = St("RDSecondaryButton") };
        _hubPause.Click += async (_, _) => await SendHubAsync(CurrentInbox()?.State?.Enabled == true ? "phone_pause" : "phone_resume");
        var saved = App.AppSettings;
        _hubPreviews = NewToggle(saved.HubPreviews);
        _hubPreviews.Toggled += (_, _) => { ClearHubAlert(); RenderPhoneInbox(); SaveHubToggles(); };
        _hubAlerts = NewToggle(saved.HubInAppAlerts);
        _hubAlerts.Toggled += (_, _) => SaveHubToggles();
        _hubShellAlerts = NewToggle(saved.HubShellAlerts);
        _hubShellAlerts.Toggled += (_, _) => { ClearHubAlert(); RefreshHubAlertTest(); SaveHubToggles(); };
        _hubFocus = NewToggle(saved.HubFocus);
        _hubFocus.Toggled += (_, _) => { if (_hubFocus.IsOn) ClearHubAlert(); RefreshHubAlertTest(); SaveHubToggles(); };
        _hubTestAlert = new Button { Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        _hubTestFeedback = Txt("", "RDTextHelper");
        _hubTestAlert.Click += (_, _) => {
            if (_hubFocus.IsOn) return;
            var submitted = (Application.Current as App)?.ShowPhoneHubTestBalloon() == true;
            var status = (Application.Current as App)?.PhoneHubAlertStatus ?? "hub.testFailed";
            _hubTestFeedback.Text = Strings.Get(status);
            Console.WriteLine($"[PhoneHub] Synthetic Windows alert submitted={submitted}; status={status}.");
        };

        // Top row: phone listbox + connection chip, then name/status and the two actions.
        var topRow = new Grid { ColumnSpacing = 12 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(_hubPhones); Grid.SetColumn(_hubChipHost, 1); topRow.Children.Add(_hubChipHost);
        var overview = new StackPanel { Spacing = 10 };
        overview.Children.Add(Txt(ConnectionStrings.Get("apprPhone"), "RDTextLabel")); overview.Children.Add(topRow); overview.Children.Add(_hubOverview); overview.Children.Add(_hubStatus);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(_hubSync); controls.Children.Add(_hubPause); overview.Children.Add(controls);

        // Features card: four toggles, each with a tile and a helper line.
        _hubRowPreviews = ToggleRow("Eye", "x", "x", _hubPreviews);
        _hubRowAlerts = ToggleRow("Bell", "x", "x", _hubAlerts);
        _hubRowShell = ToggleRow("Devices", "x", "x", _hubShellAlerts);
        _hubRowFocus = ToggleRow("Pulse", "x", "x", _hubFocus);
        var featHeader = CardHeader("PhoneHub", ""); _hubFeaturesTitle = (TextBlock)featHeader.Children[1];
        var features = new StackPanel { Spacing = 12 };
        features.Children.Add(featHeader);
        features.Children.Add(_hubRowPreviews); features.Children.Add(_hubRowAlerts); features.Children.Add(_hubRowShell); features.Children.Add(_hubRowFocus);

        // Notifications card: app listbox + search, then the rows.
        var filters = new Grid { ColumnSpacing = 10 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchHost = new Grid();
        searchHost.Children.Add(_hubSearch);
        var searchGlyph = Ico("Search", 16, "RDTextFaintBrush");
        searchGlyph.HorizontalAlignment = HorizontalAlignment.Left; searchGlyph.Margin = new Thickness(12, 0, 0, 0); searchGlyph.IsHitTestVisible = false;
        searchHost.Children.Add(searchGlyph);
        filters.Children.Add(searchHost); Grid.SetColumn(_hubApps, 1); filters.Children.Add(_hubApps);
        _hubCards = new StackPanel { Spacing = 8 };
        _hubDetail = new StackPanel { Spacing = 12 };
        var notifHeader = CardHeader("Bell", ""); _hubNotifTitle = (TextBlock)notifHeader.Children[1];
        var notifications = new StackPanel { Spacing = 12 };
        notifications.Children.Add(notifHeader); notifications.Children.Add(filters); notifications.Children.Add(_hubCards);

        // Privacy card.
        var privacyHeader = CardHeader("Shield", ""); _hubPrivacyTitle = (TextBlock)privacyHeader.Children[1];
        _hubPrivacy = Txt("", "RDTextHelper"); _hubLimits = Txt("", "RDTextHelper");
        var privacy = new StackPanel { Spacing = 10 };
        privacy.Children.Add(privacyHeader); privacy.Children.Add(_hubPrivacy); privacy.Children.Add(_hubLimits);
        privacy.Children.Add(_hubTestAlert); privacy.Children.Add(_hubTestFeedback);

        _hubLeft = new StackPanel { Spacing = 16 };
        _hubLeft.Children.Add(CardBox(features)); _hubLeft.Children.Add(CardBox(privacy));
        _hubRight = new StackPanel { Spacing = 16 };
        _hubRight.Children.Add(CardBox(notifications)); _hubRight.Children.Add(_hubDetail);
        _hubColumns = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        _hubColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        _hubColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hubColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _hubColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _hubColumns.Children.Add(_hubLeft); _hubColumns.Children.Add(_hubRight);
        var hubHeader = new StackPanel { Spacing = 6 }; hubHeader.Children.Add(_hubTitle); hubHeader.Children.Add(_hubSubtitle); PhoneHubContent.Children.Add(hubHeader);
        PhoneHubContent.Children.Add(CardBox(overview)); PhoneHubContent.Children.Add(_hubColumns);
        Loaded += (_, _) => { AttachPhoneHub(); RefreshHubConnections(); };
        Unloaded += (_, _) => DetachPhoneHub();
    }

    private void AttachPhoneHub()
    {
        if (_hubAttached) return;
        App.Session.PhoneHubEventReceived += OnPhoneHubEvent;
        App.Session.CompanionResultReceived += OnHubResult;
        App.Session.DeviceApprovalRequested += ApproveDeviceAsync;
        App.Session.ClipboardRequested += ApproveClipboardAsync;
        _hubAttached = true;
    }
    private void DetachPhoneHub()
    {
        if (!_hubAttached) return;
        App.Session.PhoneHubEventReceived -= OnPhoneHubEvent;
        App.Session.CompanionResultReceived -= OnHubResult;
        App.Session.DeviceApprovalRequested -= ApproveDeviceAsync;
        App.Session.ClipboardRequested -= ApproveClipboardAsync;
        _hubAttached = false;
        foreach (var inbox in _phoneInboxes.Values) inbox.Clear();
        _phoneInboxes.Clear(); _phoneGenerations.Clear(); ClearHubAlert();
        _hubShellGates.Clear();
        foreach (var pending in _hubPending.Values) pending.TrySetResult(new CompanionResult { Detail = Strings.Get("hub.offline") });
        _hubPending.Clear();
    }

    private void PhoneHubItem_Click(object sender, RoutedEventArgs e) { Navigate("phonehub"); RefreshHubConnections(); }
    internal void OpenPhoneHub() { Navigate("phonehub"); RefreshHubConnections(); }
    private void SaveHubToggles() =>
        App.AppSettings.SetPhoneHubToggles(_hubPreviews.IsOn, _hubAlerts.IsOn, _hubShellAlerts.IsOn, _hubFocus.IsOn);
    private void RefreshHubAlertTest()
    {
        if (_hubTestAlert is not null) _hubTestAlert.IsEnabled = !_hubFocus.IsOn;
    }
    private PhoneInbox? CurrentInbox() => _hubDevice is not null && _phoneGenerations.TryGetValue(_hubDevice, out var generation) && App.Session.IsCurrentSession(_hubDevice, generation) && _phoneInboxes.TryGetValue(_hubDevice, out var inbox) ? inbox : null;
    private bool HubConnected(string? deviceId) => deviceId is not null && App.Session.TryGetSessionState(deviceId) == SIPSorcery.Net.RTCPeerConnectionState.connected;
    private void LocalizePhoneHub()
    {
        if (_hubTitle is null) return;
        PhoneHubNavLabel.Text = CompactPhoneHubItem.Text = Strings.Get("hub.title");
        SetAccessibleName(PhoneHubItem, "hub.title");
        _hubTitle.Text = Strings.Get("hub.title"); _hubSubtitle.Text = Strings.Get("hub.subtitle");
        _hubSearch.PlaceholderText = Strings.Get("hub.search");
        AutomationProperties.SetName(_hubSearch, Strings.Get("hub.search"));
        AutomationProperties.SetName(_hubPhones, Strings.Get("hub.choosePhone"));
        AutomationProperties.SetName(_hubApps, Strings.Get("hub.filterApp"));
        _hubPhones.PlaceholderText = Strings.Get("hub.choosePhone");
        _hubSync.Content = Strings.Get("hub.sync");
        SetRowText(_hubRowPreviews, Strings.Get("hub.previews"), Strings.Get("hub.previewsHelp"));
        SetRowText(_hubRowAlerts, Strings.Get("hub.alerts"), Strings.Get("hub.alertsHelp"));
        SetRowText(_hubRowShell, Strings.Get("hub.shellAlerts"), Strings.Get("hub.shellAlertsHelp"));
        SetRowText(_hubRowFocus, Strings.Get("hub.focus"), Strings.Get("hub.focusHelp"));
        _hubFeaturesTitle.Text = Strings.Get("hub.features"); _hubNotifTitle.Text = Strings.Get("hub.notifications"); _hubPrivacyTitle.Text = Strings.Get("settings.privacy");
        _hubTestAlert.Content = Strings.Get("hub.testAlert");
        ClearHubAlert();
        _hubPrivacy.Text = Strings.Get("hub.privacy");
        _hubLimits.Text = Strings.Get("hub.limits");
        RefreshHubConnections();
    }
    private void LayoutPhoneHub()
    {
        if (_hubColumns is null) return;
        var available = Math.Max(0, ContentHost.ActualWidth - Workspace.Padding.Left - Workspace.Padding.Right);
        var wide = available >= 780;
        _hubApps.MaxWidth = Math.Clamp(available * .4, 100, 220);
        _hubApps.MinWidth = Math.Min(130, _hubApps.MaxWidth);
        _hubColumns.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _hubColumns.ColumnSpacing = wide ? 16 : 0;
        Grid.SetColumn(_hubRight, wide ? 1 : 0); Grid.SetRow(_hubRight, wide ? 0 : 1);
    }
    private void RefreshHubConnections()
    {
        if (_hubPhones is null) return;
        var devices = App.Session.TrustedDevices.GetAll();
        var live = devices.Where(d => HubConnected(d.DeviceId)).Select(d => d.DeviceId).ToHashSet();
        foreach (var key in _phoneInboxes.Keys.Where(k => !live.Contains(k) || !_phoneGenerations.TryGetValue(k, out var generation) || !App.Session.IsCurrentSession(k, generation)).ToArray())
        {
            _phoneInboxes[key].Clear(); _phoneInboxes.Remove(key); _phoneGenerations.Remove(key); _hubShellGates.Remove(key);
            if (key == _hubDevice) { _hubSelected = null; ClearHubAlert(); }
        }
        _hubRefreshing = true;
        var choices = devices.Select(device => new HubChoice(device.DeviceId, DeviceLabel(device))).ToList();
        _hubPhones.ItemsSource = choices;
        if (_hubDevice is null || !devices.Any(d => d.DeviceId == _hubDevice) ||
            (!HubConnected(_hubDevice) && live.Count > 0))
            _hubDevice = devices.FirstOrDefault(d => live.Contains(d.DeviceId))?.DeviceId ?? devices.FirstOrDefault()?.DeviceId;
        _hubPhones.SelectedIndex = choices.FindIndex(c => c.Tag == _hubDevice);
        _hubRefreshing = false;
        if (!HubConnected(_hubDevice)) ClearHubAlert();
        RefreshHubApps(); RenderPhoneInbox(); LayoutPhoneHub();
    }
    private void RefreshHubApps()
    {
        if (_hubApps is null) return;
        var selected = (_hubApps.SelectedItem as HubChoice)?.Tag;
        _hubRefreshing = true;
        var choices = new List<HubChoice> { new("", Strings.Get("hub.allApps")) };
        foreach (var app in (CurrentInbox()?.Items ?? Array.Empty<PhoneNotification>()).GroupBy(n => n.PackageName).OrderBy(g => g.First().AppName))
            choices.Add(new HubChoice(app.Key, app.First().AppName));
        _hubApps.ItemsSource = choices;
        var index = choices.FindIndex(c => c.Tag == selected);
        _hubApps.SelectedIndex = index >= 0 ? index : 0;
        _hubRefreshing = false;
    }
    private void OnPhoneHubEvent(string deviceId, string sessionId, PhoneHubEvent update)
    {
        DispatcherQueue.PostSafe(() => {
            if (!_hubAttached || !App.Session.IsCurrentSession(deviceId, sessionId) || !HubConnected(deviceId)) return;
            if (!_phoneGenerations.TryGetValue(deviceId, out var generation) || generation != sessionId)
            {
                if (_phoneInboxes.Remove(deviceId, out var previous)) previous.Clear();
                _phoneGenerations[deviceId] = sessionId;
                _hubShellGates[deviceId] = new PhoneAlertGate();
                if (deviceId == _hubDevice) { _hubSelected = null; ClearHubAlert(); }
            }
            if (!_phoneInboxes.TryGetValue(deviceId, out var inbox)) _phoneInboxes[deviceId] = inbox = new PhoneInbox();
            var previousNotification = update.Notification is null ? null : inbox.Items.FirstOrDefault(n => n.Id == update.Notification.Id);
            var wasPresent = previousNotification is not null;
            if (!inbox.Apply(update)) return;
            var shellCandidate = false;
            if (update.Kind == "upsert" && update.Notification is { } notification && _hubShellGates.TryGetValue(deviceId, out var gate))
            {
                shellCandidate = gate.Observe(previousNotification, notification, !inbox.Syncing && deviceId == _hubDevice &&
                    _hubShellAlerts.IsOn && !_hubFocus.IsOn && inbox.State is { Enabled: true, AccessGranted: true }, Environment.TickCount64);
            }
            if (_hubDevice is null) _hubDevice = deviceId;
            if (deviceId != _hubDevice) return;
            if (shellCandidate) (Application.Current as App)?.ShowPhoneHubBalloon();
            if (update.Kind == "upsert" && !wasPresent && !inbox.Syncing && _hubAlerts.IsOn && !_hubFocus.IsOn)
            {
                _hubAlertShown = true;
                ShowToast(RDToastKind.Info, (update.Notification?.AppName ?? Strings.Get("hub.title")) + ": " + (_hubPreviews.IsOn && update.Notification is { Redacted: false } ? update.Notification.Title : Strings.Get("hub.privateAlert")));
            }
            if (update.Kind == "clear" || update.State is { Enabled: false } || update.State is { AccessGranted: false }) ClearHubAlert();
            RefreshHubApps(); RenderPhoneInbox();
        });
    }
    private void ClearHubAlert()
    {
        (Application.Current as App)?.ClearPhoneHubBalloon();
        if (!_hubAlertShown) return;
        _hubAlertShown = false; _toast?.Hide();
    }
    private void OnHubResult(string deviceId, string sessionId, CompanionResult result) => DispatcherQueue.PostSafe(() => {
        if (!_hubAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return;
        if (deviceId == _hubDevice && _hubPending.Remove(result.RequestId, out var pending)) pending.TrySetResult(result);
    });

    private void RenderPhoneInbox()
    {
        if (_hubCards is null) return;
        var inbox = CurrentInbox(); var state = inbox?.State; var connected = HubConnected(_hubDevice);
        _hubChipHost.Child = Chip(Strings.Get(connected ? "hub.connected" : "hub.offline"), connected ? "RDSuccessBrush" : "RDTextFaintBrush");
        _hubOverview.Text = (_hubPhones.SelectedItem as HubChoice)?.Label is { Length: > 0 } picked ? picked : state?.PhoneName is { Length: > 0 } name ? name : Strings.Get("hub.choosePhone");
        var battery = state is { BatteryPercent: >= 0 and <= 100 } ? $" · {N(state.BatteryPercent)}%" + (state.Charging ? " · " + Strings.Get("hub.charging") : "") : "";
        // The chip already says Offline, so the subline never repeats it.
        var statusParts = new List<string>();
        if (connected) statusParts.Add(Strings.Get(state is null ? "hub.waiting" : !state.AccessGranted ? "hub.permission" : !state.Enabled ? "hub.paused" : "hub.secure") + battery);
        if (inbox?.LastSeen is { } lastSeen) statusParts.Add(string.Format(Strings.Get("hub.lastSeen"), FormatLocalDateTime(lastSeen.ToLocalTime())));
        _hubStatus.Text = string.Join(" · ", statusParts);
        _hubStatus.Visibility = statusParts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _hubPause.Content = Strings.Get(state?.Enabled == true ? "hub.pause" : "hub.resume");
        _hubSync.IsEnabled = _hubPause.IsEnabled = connected && _hubPending.Count == 0;
        _hubCards.Children.Clear();
        var selectedApp = (_hubApps.SelectedItem as HubChoice)?.Tag ?? "";
        var query = _hubSearch.Text.Trim();
        var items = (inbox?.Items ?? Array.Empty<PhoneNotification>()).Where(n =>
            (selectedApp.Length == 0 || selectedApp == n.PackageName) &&
            (query.Length == 0 || n.AppName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             (_hubPreviews.IsOn && !n.Redacted && (n.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || n.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)))))
            .OrderByDescending(n => n.PostedAtUnixMs).ToArray();
        if (items.Length == 0 && connected && inbox?.Syncing == true)
        {
            for (var i = 0; i < 3; i++) _hubCards.Children.Add(new RDSkeleton { Height = 56, Radius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        else if (items.Length == 0)
        {
            var empty = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 12, 0, 12) };
            var tile = IconTile(connected ? "Bell" : "PhoneHub", 56, 24); tile.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(tile);
            var title = new TextBlock { Text = Strings.Get(!connected ? "hub.offlineTitle" : "hub.empty"), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var line = Txt(Strings.Get(!connected ? "hub.connectHelp" : state?.AccessGranted != true ? "hub.permissionHelp" : "hub.emptyHelp"), "RDTextHelper"); line.TextAlignment = TextAlignment.Center;
            empty.Children.Add(title); empty.Children.Add(line);
            _hubCards.Children.Add(empty);
        }
        foreach (var group in items.GroupBy(n => n.PackageName))
        {
            _hubCards.Children.Add(Txt($"{group.First().AppName}  ·  {N(group.Count())}", "RDTextLabel"));
            foreach (var item in group)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 56 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = new Image { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center }; LoadHubIcon(icon, item.IconPng.ToByteArray());
                row.Children.Add(icon);
                var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                var title = HubText(_hubPreviews.IsOn && !item.Redacted ? item.Title : Strings.Get("hub.hidden"), 14, true);
                title.MaxLines = 1; title.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(title);
                var sub = HubText(item.AppName + " · " + HubTime(item.PostedAtUnixMs), 12);
                sub.MaxLines = 1; sub.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(sub);
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var card = new Button { Content = row, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush("RDPanelBrush"),
                    BorderBrush = Brush(_hubSelected == item.Id ? "RDAccentBrush" : "RDLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
                AutomationProperties.SetName(card, item.AppName + " · " + Strings.Get("hub.open"));
                card.Click += (_, _) => { _hubSelected = item.Id; RenderPhoneInbox(); };
                _hubCards.Children.Add(card);
            }
        }
        RenderHubDetail(inbox?.Items.FirstOrDefault(n => n.Id == _hubSelected));
    }
    private void RenderHubDetail(PhoneNotification? item)
    {
        _hubDetail.Children.Clear();
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock { Text = Strings.Get("hub.detail"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush") });
        if (item is null) content.Children.Add(Txt(Strings.Get("hub.select"), "RDTextHelper"));
        else
        {
            content.Children.Add(HubText(item.AppName + " · " + HubTime(item.PostedAtUnixMs), 12));
            content.Children.Add(HubText(_hubPreviews.IsOn && !item.Redacted ? item.Title : Strings.Get("hub.hidden"), 18, true));
            var bubble = HubText(_hubPreviews.IsOn && !item.Redacted ? item.Text : Strings.Get("hub.openPrivacy"), 14);
            bubble.IsTextSelectionEnabled = _hubPreviews.IsOn && !item.Redacted;
            content.Children.Add(new Border { Child = bubble, Padding = new Thickness(16), Background = Brush("RDPanelBrush"), BorderBrush = Brush("RDLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) });
            content.Children.Add(Txt(Strings.Get("hub.activeOnly"), "RDTextHelper"));
            if (item.Category == "call") content.Children.Add(Txt(Strings.Get("hub.callLimit"), "RDTextHelper"));
            foreach (var action in item.Actions)
            {
                TextBox? reply = null; RDListBox? choices = null;
                if (action.AcceptsReply && action.AllowsFreeForm)
                {
                    reply = new TextBox { Style = St("RDTextBox"), PlaceholderText = Strings.Get("hub.replyText"), MaxLength = 4096, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
                    AutomationProperties.SetName(reply, Strings.Get("hub.replyText")); content.Children.Add(reply);
                }
                if (action.AcceptsReply && action.ReplyChoices.Count > 0)
                {
                    choices = new RDListBox { ItemsSource = action.ReplyChoices.ToArray(), PlaceholderText = Strings.Get("hub.chooseReply"), HorizontalAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(choices, Strings.Get("hub.chooseReply")); content.Children.Add(choices);
                }
                var button = new Button { Content = action.Label, Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = HubConnected(_hubDevice) && _hubPending.Count == 0 };
                var capturedReply = reply; var capturedChoices = choices;
                button.Click += async (_, _) => {
                    var text = capturedReply?.Text.Trim();
                    if (string.IsNullOrEmpty(text)) text = capturedChoices?.SelectedItem as string ?? "";
                    if (action.AcceptsReply && text.Length == 0) { _hubStatus.Visibility = Visibility.Visible; _hubStatus.Text = Strings.Get("hub.replyText"); return; }
                    await SendHubAsync("phone_action", item.Id, action.Id, text ?? "");
                };
                content.Children.Add(button);
            }
            if (item.Actions.Count == 0) content.Children.Add(Txt(Strings.Get("hub.noActions"), "RDTextHelper"));
            var dismiss = new Button { Content = Strings.Get("hub.dismiss"), Style = St("RDGhostButton"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = HubConnected(_hubDevice) && _hubPending.Count == 0 };
            dismiss.Click += async (_, _) => await SendHubAsync("phone_dismiss", item.Id);
            content.Children.Add(dismiss);
        }
        _hubDetail.Children.Add(HubCard(content));
    }
    private async Task SendHubAsync(string operation, string notificationId = "", string actionId = "", string text = "")
    {
        if (_hubDevice is null || !HubConnected(_hubDevice) || _hubPending.Count > 0) return;
        var deviceId = _hubDevice;
        _phoneGenerations.TryGetValue(deviceId, out var generation);
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CompanionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hubPending[requestId] = completion;
        if (!App.Session.SendCompanionRequest(deviceId, new CompanionRequest { RequestId = requestId, Operation = operation, NotificationId = notificationId, ActionId = actionId, Text = text }))
        {
            _hubPending.Remove(requestId); _hubStatus.Visibility = Visibility.Visible; _hubStatus.Text = Strings.Get("hub.offline"); return;
        }
        RenderPhoneInbox(); _hubStatus.Visibility = Visibility.Visible; _hubStatus.Text = Strings.Get("hub.pending");
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(12)));
        _hubPending.Remove(requestId);
        if (_hubDevice != deviceId || (generation is not null && !App.Session.IsCurrentSession(deviceId, generation))) return;
        RenderPhoneInbox();
        if (finished != completion.Task) { _hubStatus.Visibility = Visibility.Visible; _hubStatus.Text = Strings.Get("hub.timeout"); }
        else
        {
            var result = await completion.Task;
            _hubStatus.Visibility = Visibility.Visible; _hubStatus.Text = Strings.Get(result.Success ? "hub.actionDone" : "hub.actionFailed") + (result.Detail.Length > 0 ? " · " + result.Detail[..Math.Min(result.Detail.Length, 512)] : "");
        }
    }
    private TextBlock HubText(string text, double size = 13, bool strong = false) => new() {
        Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush(strong ? "RDTextBrush" : "RDTextMutedBrush"), TextWrapping = TextWrapping.Wrap,
        TextReadingOrder = TextReadingOrder.DetectFromContent
    };
    private Border HubCard(UIElement child) => CardBox(child);
    private static string HubTime(long time)
    {
        try { return FormatLocalDateTime(DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime()); }
        catch (ArgumentOutOfRangeException) { return Dash; }
    }
    private static async void LoadHubIcon(Image image, byte[] png)
    {
        if (png.Length is < 8 or > 32768 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71) { image.Visibility = Visibility.Collapsed; return; }
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(png); await writer.StoreAsync(); await writer.FlushAsync(); }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 32, DecodePixelHeight = 32 };
            await bitmap.SetSourceAsync(stream); image.Source = bitmap;
        }
        catch (Exception) { image.Visibility = Visibility.Collapsed; }
    }

    private Task<bool> ApproveDeviceAsync(DeviceIdentity device) => OnHubUiAsync(async cancellation => {
        await _consentDialog.WaitAsync(cancellation);
        try
        {
            (Application.Current as App)?.ShowMainWindow();
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(HubText(device.DisplayName[..Math.Min(device.DisplayName.Length, 128)], 18, true));
            body.Children.Add(HubText(Strings.Get("hub.trustBody"), 14));
            body.Children.Add(Mono(device.DeviceId[..Math.Min(device.DeviceId.Length, 128)]));
            var dialog = Dlg(Strings.Get("hub.trustTitle"), body, Strings.Get("hub.trustAllow"), Strings.Get("common.cancel"));
            using var expiry = cancellation.Register(() => DispatcherQueue.PostSafe(dialog.Hide));
            if (cancellation.IsCancellationRequested) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellation.IsCancellationRequested;
        }
        finally { _consentDialog.Release(); }
    }, false);

    private Task<CompanionResult> ApproveClipboardAsync(string deviceId, bool read, string text, Func<Action, bool> authorized) => OnHubUiAsync(async cancellation => {
        if (text.Length > 8192) return new CompanionResult { Detail = Strings.Get("hub.clipboardLarge") };
        await _consentDialog.WaitAsync(cancellation);
        try
        {
            (Application.Current as App)?.ShowMainWindow();
            var body = new StackPanel { Spacing = 12 };
            var device = App.Session.TrustedDevices.GetAll().FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is null) return new CompanionResult { Detail = Strings.Get("hub.denied") };
            body.Children.Add(HubText(DeviceLabel(device), 17, true));
            body.Children.Add(HubText(Strings.Get(read ? "hub.clipboardRead" : "hub.clipboardWrite"), 14));
            var dialog = Dlg(Strings.Get("hub.clipboard"), body, Strings.Get("hub.allow"), Strings.Get("common.cancel"));
            using var expiry = cancellation.Register(() => DispatcherQueue.PostSafe(dialog.Hide));
            if (cancellation.IsCancellationRequested) return new CompanionResult { Detail = Strings.Get("hub.denied") };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested) return new CompanionResult { Detail = Strings.Get("hub.denied") };
            if (read)
            {
                if (cancellation.IsCancellationRequested) return new CompanionResult { Detail = Strings.Get("hub.denied") };
                DataPackageView? clipboard = null;
                if (!authorized(() => clipboard = Clipboard.GetContent()) || clipboard is null) return new CompanionResult { Detail = Strings.Get("hub.denied") };
                if (!clipboard.Contains(StandardDataFormats.Text)) return new CompanionResult { Detail = Strings.Get("hub.clipboardEmpty") };
                var value = await clipboard.GetTextAsync();
                if (cancellation.IsCancellationRequested || !authorized(() => { })) return new CompanionResult { Detail = Strings.Get("hub.denied") };
                if (value.Length > 8192) return new CompanionResult { Detail = Strings.Get("hub.clipboardLarge") };
                return new CompanionResult { Success = true, Detail = value };
            }
            if (cancellation.IsCancellationRequested) return new CompanionResult { Detail = Strings.Get("hub.denied") };
            var data = new DataPackage(); data.SetText(text);
            if (!authorized(() => Clipboard.SetContent(data))) return new CompanionResult { Detail = Strings.Get("hub.denied") };
            return new CompanionResult { Success = true, Detail = Strings.Get("hub.actionDone") };
        }
        finally { _consentDialog.Release(); }
    }, new CompanionResult { Detail = Strings.Get("hub.denied") });

    private async Task<T> OnHubUiAsync<T>(Func<CancellationToken, Task<T>> action, T denied)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.PostSafe(async () => {
            try { completion.TrySetResult(await action(timeout.Token)); }
            catch (Exception) { completion.TrySetResult(denied); Console.WriteLine("[PhoneHub] Consent or clipboard operation unavailable."); }
        })) completion.TrySetResult(denied);
        return await completion.Task;
    }
}

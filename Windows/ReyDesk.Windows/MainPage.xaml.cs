using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Devices;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Settings;
using Windows.ApplicationModel.DataTransfer;

namespace ReyDesk.Windows;

/// <summary>Local pairing, live sessions and preferences in a responsive native workspace.</summary>
public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, DateTimeOffset> _connectedSince = new();
    private DateTimeOffset _offerExpiresAt;
    private string _selectedPage = "connection";
    private bool _devicesLoading = true;
    private bool _languageChanged;
    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public MainPage()
    {
        InitializeComponent();
        InitializeToast();
        _countdownTimer.Tick += CountdownTimer_Tick;
        InitializePhoneHub();
        InitializeTransfers();
        InitializeApprovedActions();
        InitializeDeveloperMode();
        ApplyLocalization();
        App.Session.TrustedDevices.Changed += OnTrustedDevicesChanged;
        App.Session.SessionStateChanged += OnSessionStateChanged;
        App.Session.StreamStatsChanged += OnStreamStatsChanged;
        RemoteUiState.Current.Changed += () => DispatcherQueue.PostSafe(RefreshInternetSection);
        InitializeRemoteBridge();
        RefreshDevicesList();
        var loading = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        loading.Tick += (_, _) => { loading.Stop(); _devicesLoading = false; RefreshDevicesList(); };
        loading.Start();
    }

    private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Sidebar is null) return;
        ApplyShellMetrics(e.NewSize.Width);
        UpdatePairingLayout();
    }

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePairingLayout();
    private void UpdatePairingLayout()
    {
        if (PairingLayout is null || Workspace.ActualWidth <= 0) return;
        var available = Math.Max(0, ContentHost.Width - Workspace.Padding.Left - Workspace.Padding.Right - 16);
        ConnectionContent.Width = Math.Min(available, 1000);
        SettingsContent.Width = Math.Min(available, 760);
        DevicesContent.Width = Math.Min(available, 1000);
        LayoutDeviceCards();
        LayoutPhoneHub();
        available = ConnectionContent.Width;
        var sideBySide = available >= 660;
        PairingLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        PairingLayout.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
        PairingLayout.ColumnSpacing = sideBySide ? 16 : 0;
        Grid.SetColumn(DetailsPanel, sideBySide ? 1 : 0);
        Grid.SetRow(DetailsPanel, sideBySide ? 0 : 1);
        QrPanel.MaxWidth = sideBySide ? double.PositiveInfinity : 440;
        QrPanel.Width = DetailsPanel.Width = sideBySide ? double.NaN : Math.Min(available, 440);
        DetailsPanel.MaxWidth = sideBySide ? double.PositiveInfinity : 440;
        QrPanel.HorizontalAlignment = sideBySide ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        DetailsPanel.HorizontalAlignment = sideBySide ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        // A fixed 224-DIP QR must never force the card beyond a narrow viewport.
        QrCodeImage.Width = QrCodeImage.Height = Math.Clamp(available - 88, 120, 224);
    }

    private void ConnectionItem_Click(object sender, RoutedEventArgs e) => Navigate("connection");
    private void DevicesItem_Click(object sender, RoutedEventArgs e) => Navigate("devices");
    private void SettingsItem_Click(object sender, RoutedEventArgs e) => Navigate("settings");
    private void TutorialItem_Click(object sender, RoutedEventArgs e) => OpenTutorial();
    private void PairDeviceButton_Click(object sender, RoutedEventArgs e) => Navigate("connection");
    private void Navigate(string page)
    {
        _selectedPage = page;
        ConnectionPanel.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPanel.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PhoneHubPanel.Visibility = page == "phonehub" ? Visibility.Visible : Visibility.Collapsed;
        TransfersPanel.Visibility = page == "transfers" ? Visibility.Visible : Visibility.Collapsed;
        TutorialPanel.Visibility = page == "tutorial" ? Visibility.Visible : Visibility.Collapsed;
        SetNavState(TutorialItem, TutorialNavSurface, TutorialNavRail, TutorialNavIcon, TutorialNavLabel, page == "tutorial");
        RefreshFirstRunCard();
        SetNavState(TransfersItem, TransfersNavSurface, TransfersNavRail, TransfersNavIcon, TransfersNavLabel, page == "transfers");
        SetNavState(PhoneHubItem, PhoneHubNavSurface, PhoneHubNavRail, PhoneHubNavIcon, PhoneHubNavLabel, page == "phonehub");
        SetNavState(ConnectionItem, ConnectionNavSurface, ConnectionNavRail, ConnectionNavIcon, ConnectionNavLabel, page == "connection");
        SetNavState(DevicesItem, DevicesNavSurface, DevicesNavRail, DevicesNavIcon, DevicesNavLabel, page == "devices");
        SetNavState(SettingsItem, SettingsNavSurface, SettingsNavRail, SettingsNavIcon, SettingsNavLabel, page == "settings");
    }
    private void SetNavState(Button button, Grid surface, UIElement rail, Controls.RDIcon icon, TextBlock label, bool selected)
    {
        surface.Background = Brush(selected ? "RDNavSelectedBrush" : "RDSidebarBrush");
        icon.Foreground = label.Foreground = Brush(selected ? "RDTextBrush" : "RDTextMutedBrush");
        rail.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(button, selected ? Strings.Get("nav.selected") : "");
    }

    private void RegenerateButton_Click(object sender, RoutedEventArgs e) => _ = RefreshPairingOfferAsync();
    private async Task RefreshPairingOfferAsync()
    {
        var presentation = App.Session.CreatePairingOffer();
        QrCodeImage.Source = await QrImageRenderer.RenderAsync(presentation.QrPayload);
        _offerExpiresAt = presentation.ExpiresAt;
        UpdateCountdownText();
        _countdownTimer.Start();
    }
    private void CountdownTimer_Tick(object? sender, object e)
    {
        UpdateSessionDuration();
        var remaining = _offerExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _countdownTimer.Stop();
            _ = RefreshPairingOfferAsync();
            return;
        }
        UpdateCountdownText(remaining);
    }
    private void UpdateCountdownText(TimeSpan? remaining = null)
    {
        var value = remaining ?? _offerExpiresAt - DateTimeOffset.UtcNow;
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        ExpiryText.Text = string.Format(Strings.Get("connection.expiresIn"), N($"{(int)value.TotalMinutes}:{value.Seconds:D2}"));
    }
    private void CopyNameButton_Click(object sender, RoutedEventArgs e) => CopyText(App.DeviceIdentity.DisplayName);
    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
    private void DisconnectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in App.Session.TrustedDevices.GetAll().ToArray())
            if (IsActive(App.Session.TryGetSessionState(device.DeviceId))) App.Session.DisconnectDevice(device.DeviceId);
    }
    private static bool IsActive(SIPSorcery.Net.RTCPeerConnectionState? state) => state is SIPSorcery.Net.RTCPeerConnectionState.connected or SIPSorcery.Net.RTCPeerConnectionState.connecting or SIPSorcery.Net.RTCPeerConnectionState.@new;
    private void OnTrustedDevicesChanged() => DispatcherQueue.PostSafe(RefreshDevicesList);
    private void OnSessionStateChanged(string deviceId) => DispatcherQueue.PostSafe(() => { RefreshDevicesList(); RefreshHubConnections(); RefreshTransferConnections(); });
    private void OnStreamStatsChanged(string deviceId) => DispatcherQueue.PostSafe(UpdateConnectionMetrics);

    private void RefreshDevicesList()
    {
        var devices = App.Session.TrustedDevices.GetAll();
        DevicesEmptyState.Visibility = devices.Count == 0 && !_devicesLoading ? Visibility.Visible : Visibility.Collapsed;
        DevicesListHost.Children.Clear();
        if (_devicesLoading) { for (var i = 0; i < 3; i++) DevicesListHost.Children.Add(SkeletonCard(320)); }
        else foreach (var device in devices) DevicesListHost.Children.Add(CreateDeviceRow(device));
        LayoutDeviceCards();
        var connected = devices.Where(d => App.Session.TryGetSessionState(d.DeviceId) == SIPSorcery.Net.RTCPeerConnectionState.connected).ToArray();
        var active = devices.Where(d => IsActive(App.Session.TryGetSessionState(d.DeviceId))).ToArray();
        foreach (var id in _connectedSince.Keys.ToArray())
            if (!connected.Any(d => d.DeviceId == id)) _connectedSince.Remove(id);
        foreach (var device in connected) _connectedSince.TryAdd(device.DeviceId, DateTimeOffset.UtcNow);
        ConnectionCountText.Text = string.Format(Strings.Get("connection.connectedCount"), N(connected.Length));
        ConnectionSummaryText.Text = Strings.Get(active.Length == 0 ? "connection.noneConnected" : "connection.sessionActive");
        DisconnectAllButton.IsEnabled = active.Length > 0;
        PairingLayout.Visibility = connected.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanel.Visibility = connected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionTitle.Text = connected.Length > 0 ? string.Format(Strings.Get("connection.connectedTo"), DeviceLabel(connected[0])) : Strings.Get("connection.title");
        if (connected.Length > 0)
        {
            ConnectedDeviceName.Text = DeviceLabel(connected[0]);
            ConnectedArtworkHost.Children.Clear();
            ConnectedArtworkHost.Children.Add(CreateDeviceArtwork(connected[0].DisplayName, connected[0].DeviceId, 170));
        }
        ConnectionFooter.Visibility = connected.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ConnectionSubtitle.Text = Strings.Get(connected.Length > 0 ? "connection.localConnected" : "connection.subtitle");
        UpdateSessionDuration();
        UpdateConnectionMetrics();
    }
    private void UpdateSessionDuration()
    {
        if (_connectedSince.Count == 0) { SessionDurationText.Text = ""; return; }
        var elapsed = DateTimeOffset.UtcNow - _connectedSince.Values.Min();
        SessionDurationText.Text = string.Format(Strings.Get("connection.duration"), N($"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"));
    }
    /// <summary>Generic, model-agnostic device art drawn in code: dark body, amber rim, tinted screen, camera dot.</summary>
    private FrameworkElement CreateDeviceArtwork(string name, string seed, double height)
    {
        var kind = DeviceArtwork.GetKind(name);
        var width = Math.Round(height * DeviceArtwork.GetAspect(kind));
        var (r, g, b) = DeviceArtwork.HslToRgb(DeviceArtwork.GetHue(seed), .55, .62);
        var tint = global::Windows.UI.Color.FromArgb(255, r, g, b);
        var radius = kind == DeviceArtwork.DeviceKind.Tablet ? height * .07 : height * .11;
        var bezel = Math.Max(4, height * .035);
        var screenGradient = new LinearGradientBrush { StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1) };
        screenGradient.GradientStops.Add(new GradientStop { Color = global::Windows.UI.Color.FromArgb(120, r, g, b), Offset = 0 });
        screenGradient.GradientStops.Add(new GradientStop { Color = global::Windows.UI.Color.FromArgb(18, r, g, b), Offset = 1 });
        var screen = new Grid
        {
            Background = Brush("RDSurfaceBrush"), CornerRadius = new CornerRadius(Math.Max(4, radius - bezel)),
            Children =
            {
                new Border { Background = screenGradient, CornerRadius = new CornerRadius(Math.Max(4, radius - bezel)) },
                new RDIcon { Glyph = kind == DeviceArtwork.DeviceKind.Tablet ? "Devices" : "PhoneHub", Size = Math.Max(16, height * .2),
                    Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        var camera = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = Math.Max(4, height * .028), Height = Math.Max(4, height * .028),
            Fill = Brush("RDLineStrongBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Max(2, bezel * .55), 0, 0) };
        var body = new Grid
        {
            Width = width, Height = height, FlowDirection = FlowDirection.LeftToRight,
            Children =
            {
                new Border { CornerRadius = new CornerRadius(radius), Background = Brush("RDPanelBrush"), BorderBrush = Brush("RDAccentBrush"), BorderThickness = new Thickness(1.5) },
                new Border { Margin = new Thickness(bezel), CornerRadius = new CornerRadius(Math.Max(4, radius - bezel)), Child = screen },
                camera
            }
        };
        AutomationProperties.SetAccessibilityView(body, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return body;
    }

    private async Task ForgetDeviceAsync(TrustedDeviceRecord device)
    {
        var dialog = Dlg(Strings.Get("devices.forget"), string.Format(Strings.Get("devices.forgetBody"), DeviceLabel(device)), Strings.Get("devices.forget"), Strings.Get("common.cancel"), danger: true);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            App.Session.DisconnectDevice(device.DeviceId);
            App.Session.TrustedDevices.Remove(device.DeviceId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrustedDevices] Could not forget device: {ex}");
            ShowToast(RDToastKind.Error, Strings.Get("devices.forgetFailed") + ": " + ex.Message);
        }
    }

    private void LayoutDeviceCards()
    {
        if (DevicesListHost is null) return;
        var cols = DevicesContent.Width >= 760 ? 3 : DevicesContent.Width >= 640 ? 2 : 1;
        DevicesListHost.ColumnDefinitions.Clear(); DevicesListHost.RowDefinitions.Clear();
        for (var i = 0; i < cols; i++) DevicesListHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (DevicesListHost.Children.Count + cols - 1) / cols; i++) DevicesListHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < DevicesListHost.Children.Count; i++) { Grid.SetRow((FrameworkElement)DevicesListHost.Children[i], i / cols); Grid.SetColumn((FrameworkElement)DevicesListHost.Children[i], i % cols); }
    }
    private void UpdateConnectionMetrics()
    {
        var device = App.Session.TrustedDevices.GetAll().FirstOrDefault(d => App.Session.TryGetSessionState(d.DeviceId) == SIPSorcery.Net.RTCPeerConnectionState.connected);
        var stats = device is null ? null : App.Session.TryGetStreamStats(device.DeviceId);
        var config = device is null ? null : App.Session.TryGetNegotiatedConfig(device.DeviceId);
        NegotiatedTargetText.Text = config is null ? Strings.Get("stats.waiting") : string.Format(Strings.Get("stats.target"), $"{config.WidthPx} × {config.HeightPx}", config.Fps, config.TargetBitrateKbps / 1000.0);
        ConnectionMetricsHost.Children.Clear(); ConnectionMetricsHost.RowDefinitions.Clear(); ConnectionMetricsHost.ColumnDefinitions.Clear();
        var labels = new[] { "stats.resolution", "stats.fps", "stats.rate", "stats.ping", "stats.loss", "stats.jitter" }.Select(Strings.Get).ToArray();
        var values = new[] { stats is { Width: > 0 } ? N($"{stats.Width} × {stats.Height}") : Dash, stats?.Fps is { } fps ? N($"{fps:0.0} fps") : Dash, stats?.BitrateKbps is { } rate ? N($"{rate / 1000:0.00} Mbps") : Dash, stats?.RoundTripTimeMs is { } ping ? N($"{ping:0} ms") : Dash, stats?.PacketLossPercent is { } loss ? N($"{loss:0.0}%") : Dash, stats?.JitterMs is { } jitter ? N($"{jitter:0.0} ms") : Dash };
        for (var i = 0; i < 2; i++) ConnectionMetricsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ConnectionMetricsHost.ColumnSpacing = 10; ConnectionMetricsHost.RowSpacing = 10;
        for (var i = 0; i < 3; i++) ConnectionMetricsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < labels.Length; i++) {
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(Txt(labels[i], "RDTextLabel"));
            content.Children.Add(new TextBlock { Text = values[i], FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
            var tile = CardBox(content, new Thickness(14, 12, 14, 12));
            Grid.SetRow(tile, i / 2); Grid.SetColumn(tile, i % 2); ConnectionMetricsHost.Children.Add(tile);
        }
    }
    private void ApplyLocalization()
    {
        FlowDirection = Strings.IsRtl(App.AppSettings.Language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ConnectionNavLabel.Text = CompactConnectionItem.Text = Strings.Get("nav.connection");
        DevicesNavLabel.Text = CompactDevicesItem.Text = Strings.Get("nav.devices");
        SettingsNavLabel.Text = CompactSettingsItem.Text = Strings.Get("nav.settings");
        TutorialNavLabel.Text = CompactTutorialItem.Text = TutorialStrings.Get("navItem");
        AutomationProperties.SetName(TutorialItem, TutorialStrings.Get("navItem"));
        LocalizeTutorial();
        SetAccessibleName(ConnectionItem, "nav.connection"); SetAccessibleName(DevicesItem, "nav.devices"); SetAccessibleName(SettingsItem, "nav.settings"); SetAccessibleName(CompactMenuButton, "nav.menu");
        ComputerNameText.Text = App.DeviceIdentity.DisplayName;
        ComputerNameLabel.Text = Strings.Get("connection.computerName");
        StatusLabel.Text = Strings.Get("connection.status");
        LocalStatusText.Text = Strings.Get("connection.localAvailable");
        ConnectionSubtitle.Text = Strings.Get("connection.subtitle");
        ConnectionStepOne.Text = Strings.Get("connection.stepOne");
        ConnectionStepTwo.Text = Strings.Get("connection.stepTwo");
        RegenerateLabel.Text = Strings.Get("connection.regenerate");
        SetAccessibleName(CopyNameButton, "connection.copyName");
        ToolTipService.SetToolTip(CopyNameButton, Strings.Get("connection.copyName"));
        DisconnectAllButton.Content = ConnectedDisconnectButton.Content = Strings.Get("devices.disconnect");
        ConnectedStatusText.Text = Strings.Get("devices.sharing");
        ConnectedLocalText.Text = Strings.Get("connection.localConnected");
        DevicesTitle.Text = Strings.Get("devices.title"); DevicesBody.Text = Strings.Get("devices.body");
        DevicesPageTitle.Text = Strings.Get("nav.devices"); DevicesPageSubtitle.Text = Strings.Get("devices.subtitle");
        PairDeviceButton.Content = AddDeviceContent(); AddDeviceButton.Content = AddDeviceContent(); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PairDeviceButton, Strings.Get("devices.add").TrimStart('+', ' ')); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddDeviceButton, Strings.Get("devices.add").TrimStart('+', ' '));
        SettingsPageTitle.Text = Strings.Get("nav.settings"); SettingsPageSubtitle.Text = Strings.Get("settings.subtitle");
        RefreshSidebarFooter();
        BuildSettingsSections(); RefreshDevicesList(); LocalizePhoneHub(); LocalizeTransfers(); Navigate(_selectedPage);
        _ = RefreshPairingOfferAsync();
    }
    private static void SetAccessibleName(DependencyObject element, string key) => AutomationProperties.SetName(element, Strings.Get(key));

    private void BuildSettingsSections()
    {
        SettingsSectionsHost.Children.Clear();
        AddLanguageSection();
        AddStartupSection();
        AddInternetSection();
        AddStreamingSection();
        AddDiagnosticsSection();
        AddTutorialHelpSection();
        AddApprovedActionsSection();
        AddDeveloperSection();
        AddRecoveryStatusSection();
        AddUpdateSection();
        AddAboutSection();
        SettingsSectionsHost.Children.Add(new HyperlinkButton { Content = string.Format(Strings.Get("settings.copyright"), N(DateTime.Now.Year)), NavigateUri = new Uri("https://reyden.ir"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Foreground = Brush("RDTextFaintBrush") });
    }

    private static RDListBox NewList(IEnumerable<string> items, int selected, string? placeholder = null) =>
        new() { ItemsSource = items.ToArray(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = placeholder ?? "" };

    private void AddStreamingSection()
    {
        var panel = new StackPanel { Spacing = 14 };
        var presets = new[] { "Auto", "Performance", "Balanced", "Quality", "Custom" };
        var selectedPreset = Array.IndexOf(presets, App.AppSettings.StreamPreset);
        if (selectedPreset < 0) selectedPreset = 4;
        var heights = new[] { 540, 720, 1080, 1440, 2160 };
        var rates = new[] { 15, 30, 60 };
        var bitrates = new[] { 4000, 8000, 12000, 20000, 30000, 50000 };
        var resolution = NewList(heights.Select(x => N($"{x}p")), Array.IndexOf(heights, App.AppSettings.StreamMaxHeight));
        var fps = NewList(rates.Select(x => N($"{x} fps")), Array.IndexOf(rates, App.AppSettings.StreamMaxFps));
        var bitrate = NewList(bitrates.Select(x => N($"{x / 1000} Mbps")), Array.IndexOf(bitrates, App.AppSettings.StreamBitrateKbps));
        var presetIndex = selectedPreset;
        bool applyingPreset = false;
        void UpdateCustomControls() => resolution.IsEnabled = fps.IsEnabled = bitrate.IsEnabled = presetIndex == 4;
        void Save()
        {
            if (applyingPreset || resolution.SelectedIndex < 0 || fps.SelectedIndex < 0 || bitrate.SelectedIndex < 0) return;
            App.AppSettings.SetStreaming(heights[resolution.SelectedIndex], rates[fps.SelectedIndex], bitrates[bitrate.SelectedIndex]);
        }
        resolution.SelectionChanged += (_, _) => Save(); fps.SelectionChanged += (_, _) => Save(); bitrate.SelectionChanged += (_, _) => Save();

        var segmented = new Grid();
        var radios = new RadioButton[presets.Length];
        for (var i = 0; i < presets.Length; i++)
        {
            var index = i;
            segmented.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            radios[i] = new RadioButton { GroupName = "rd-stream-preset", Style = St("RDSegmentedItem"), Content = new TextBlock { Text = Strings.Get("preset." + presets[i]), TextTrimming = TextTrimming.CharacterEllipsis }, IsChecked = i == selectedPreset, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(radios[i], Strings.Get("preset." + presets[i]));
            radios[i].Checked += (_, _) =>
            {
                presetIndex = index;
                applyingPreset = true;
                try
                {
                    App.AppSettings.SetStreamPreset(presets[index]);
                    resolution.SelectedIndex = Array.IndexOf(heights, App.AppSettings.StreamMaxHeight);
                    fps.SelectedIndex = Array.IndexOf(rates, App.AppSettings.StreamMaxFps);
                    bitrate.SelectedIndex = Array.IndexOf(bitrates, App.AppSettings.StreamBitrateKbps);
                    UpdateCustomControls();
                }
                finally { applyingPreset = false; }
            };
            Grid.SetColumn(radios[i], i); segmented.Children.Add(radios[i]);
        }
        UpdateCustomControls();
        var presetBox = new StackPanel { Spacing = 8 };
        presetBox.Children.Add(Txt(Strings.Get("settings.preset"), "RDTextLabel"));
        presetBox.Children.Add(new Border { Style = St("RDSegmentedContainer"), Child = segmented });
        panel.Children.Add(presetBox);
        panel.Children.Add(Txt(Strings.Get("settings.presetHint"), "RDTextHelper"));
        panel.Children.Add(SettingRow("settings.resolution", resolution));
        panel.Children.Add(SettingRow("settings.frameRate", fps));
        panel.Children.Add(SettingRow("settings.bitrate", bitrate));
        panel.Children.Add(Txt(Strings.Get("settings.streamingNextSession"), "RDTextHelper"));
        AddCard("settings.streaming", "Pulse", panel);
    }

    private void AddLanguageSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        var language = NewList(new[] { Strings.Get("language.english"), Strings.Get("language.persian") }, (int)App.AppSettings.Language);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedIndex < 0 || language.SelectedIndex == (int)App.AppSettings.Language) return;
            App.AppSettings.SetLanguage((AppLanguage)language.SelectedIndex);
            Strings.Current = App.AppSettings.Language;
            _languageChanged = true;
            ApplyLocalization();
        };
        language.HorizontalAlignment = HorizontalAlignment.Stretch; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(language, Strings.Get("settings.language")); panel.Children.Add(language);
        if (_languageChanged) panel.Children.Add(Chip(Strings.Get("settings.languageNotice"), "RDWarningBrush"));
        AddCard("settings.language", "Globe", panel);
    }

    private void AddStartupSection()
    {
        var panel = new StackPanel { Spacing = 14 };
        var startup = NewToggle(StartupService.IsEnabled());
        startup.Toggled += (_, _) => StartupService.SetEnabled(startup.IsOn);
        panel.Children.Add(ToggleRow("Update", Strings.Get("settings.startWithWindows"), Strings.Get("settings.afterSignIn"), startup));
        var close = NewList(new[] { Strings.Get("closeButton.exit"), Strings.Get("closeButton.minimize") }, (int)App.AppSettings.CloseButtonBehavior);
        close.SelectionChanged += (_, _) => { if (close.SelectedIndex >= 0) App.AppSettings.SetCloseButtonBehavior((CloseButtonBehavior)close.SelectedIndex); };
        panel.Children.Add(SettingRow("settings.closeButton", close));
        AddCard("settings.startBehavior", "Power", panel);
    }

    private void AddAboutSection()
    {
        var assembly = typeof(App).Assembly;
        var version = ReyDesk.Windows.Platform.AppInfo.Version;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "ReyDesk", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(Txt(ConnectionStrings.Get("byReyDen"), "RDTextMuted"));
        panel.Children.Add(new HyperlinkButton { Content = "reyden.ir/reydesk", NavigateUri = new Uri(ReyDesk.Windows.Updates.UpdateChecker.PageUrl), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, FlowDirection = FlowDirection.LeftToRight });
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "";
        var facts = new StackPanel { Spacing = 4 };
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        versionRow.Children.Add(Txt(Strings.Get("settings.version"), "RDTextLabel"));
        versionRow.Children.Add(Mono(version));
        var buildRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buildRow.Children.Add(Txt(Strings.Get("settings.build"), "RDTextLabel"));
        buildRow.Children.Add(Mono(configuration + " " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
        facts.Children.Add(versionRow); facts.Children.Add(buildRow);
        panel.Children.Add(facts);
        panel.Children.Add(Txt(Strings.Get("settings.localData"), "RDTextHelper"));
        panel.Children.Add(Txt(Strings.Get("settings.licenses"), "RDTextHelper"));
        var licenses = new Button { Content = Strings.Get("settings.openLicenses"), Style = St("RDSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        licenses.Click += async (_, _) =>
        {
            try
            {
                var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "LICENSE")) + Environment.NewLine + Environment.NewLine
                    + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md"));
                await Dlg(Strings.Get("settings.openLicenses"), new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, Style = St("RDTextMuted"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FlowDirection = FlowDirection.LeftToRight } }, null, Strings.Get("common.close")).ShowAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Licenses] Could not read licence files: {ex}");
                ShowToast(RDToastKind.Error, Strings.Get("settings.openLicenses") + ": " + ex.Message);
            }
        };
        panel.Children.Add(licenses);
        AddCard("settings.about", "Info", panel);
    }
}

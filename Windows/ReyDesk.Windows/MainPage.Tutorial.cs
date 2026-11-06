using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;

namespace ReyDesk.Windows;

/// <summary>
/// "How to connect" tutorial: path chooser, step card with an "On this PC" card and an "On your phone"
/// card, step list, Back/Next, "Show me" highlight, Connect-page help link, first-run card and the Settings help card.
/// Strings live in Localization/Strings.Tutorial.cs. The chosen path and step are kept in memory while the app runs.
/// </summary>
public sealed partial class MainPage
{
    private sealed record TutStep(string Id, string? Show = null, string? Link = null, bool Fork = false);

    private string? _tutPath;
    private bool _tutAfter;
    private string _tutFork = "qr";
    private int _tutIndex;
    private bool _tutBuilt, _tutBusy, _tutHooked;
    private int _highlightToken;

    private readonly List<RadioButton> _tutRadios = new();
    private readonly RDProgress _tutProgress = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _tutProgressLabel = new() { FontSize = 12 };
    private readonly Border _tutCard = new() { Style = (Style)Application.Current.Resources["RDCard"], Padding = new Thickness(18, 16, 18, 16) };
    private readonly StackPanel _tutListPanel = new() { Spacing = 2 };
    private readonly ContentControl _tutListSlot = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Expander _tutExpander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _tutColumn = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _tutDetail = new() { Spacing = 14 };
    private readonly Grid _tutBody = new();
    private Grid? _tutCards;
    private Border? _tutOtherCard;
    private int _tutStepCount;

    private static string T(string key) => TutorialStrings.Get(key);

    // ---- Step model ----

    private List<TutStep> TutSteps()
    {
        var after = new[] { new TutStep("d1", "nav:connection"), new TutStep("d2"), new TutStep("d3", "nav:devices"), new TutStep("d4", "nav:devices") };
        var l = new List<TutStep>();
        if (_tutAfter) { l.AddRange(after); return l; }
        switch (_tutPath)
        {
            case "internet":
                l.Add(new("b1", "remote")); l.Add(new("b2", "copyid")); l.Add(new("b3", Fork: true));
                if (_tutFork == "code") { l.Add(new("b4c", "addphone")); l.Add(new("b5c")); l.Add(new("b6c")); }
                else l.Add(new("b4q", "qr"));
                l.Add(new("b7", Link: "mobile"));
                l.AddRange(after);
                break;
            case "mobile":
                l.Add(new("c1")); l.Add(new("c2", "relay")); l.Add(new("c3")); l.Add(new("c4", Link: "internet"));
                break;
            default:
                l.Add(new("a1", "nav:connection")); l.Add(new("a2")); l.Add(new("a3", Fork: true));
                if (_tutFork == "nearby") { l.Add(new("a4n", "nav:connection")); l.Add(new("a5n")); }
                else l.Add(new("a4q", "qr"));
                l.Add(new("a6"));
                l.AddRange(after);
                break;
        }
        return l;
    }

    // ---- Entry points ----

    private void ConnectionHelp_Click(object sender, RoutedEventArgs e) => StartTour(DefaultTutorialPath());

    private static string DefaultTutorialPath() => RemoteUiState.Current.RemoteAllowed ? "internet" : "wifi";

    /// <summary>Open the tutorial. A path starts that guide from step 1; no path restores where the user left off.</summary>
    private void OpenTutorial(string? path = null)
    {
        if (path is not null) { _tutPath = path; _tutAfter = false; _tutIndex = 0; _tutFork = "qr"; }
        else _tutPath ??= DefaultTutorialPath();
        if (path is not null || !_tutBuilt) BuildTutorial();
        Navigate("tutorial");
    }

    private void LocalizeTutorial()
    {
        var help = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        help.Children.Add(new RDIcon { Glyph = "Help", Size = 16, VerticalAlignment = VerticalAlignment.Center });
        help.Children.Add(new TextBlock { Text = T("help"), VerticalAlignment = VerticalAlignment.Center });
        ConnectionHelpButton.Content = help;
        AutomationProperties.SetName(ConnectionHelpButton, T("help"));
        BuildFirstRunCard();
        if (_tutBuilt) BuildTutorial();
        if (!_tutHooked)
        {
            _tutHooked = true;
            App.Session.TrustedDevices.Changed += () => DispatcherQueue.PostSafe(RefreshFirstRunCard);
            // First launch: start the guided tour by itself once, while the tutorial was never seen and no phone is paired.
            Loaded += (_, _) => DispatcherQueue.PostSafe(() =>
            {
                if (App.AppSettings.TutorialSeen || System.Linq.Enumerable.Any(App.Session.TrustedDevices.GetAll())) return;
                App.AppSettings.SetTutorialSeen(true);
                StartTour(DefaultTutorialPath());
            });
        }
    }

    private void AddTutorialHelpSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Txt(T("settingsBody"), "RDTextMuted"));
        var open = new Button { Style = St("RDSecondaryButton"), Content = T("settingsOpen"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => StartTour(DefaultTutorialPath());
        panel.Children.Add(open);
        SettingsSectionsHost.Children.Add(BuildCard(T("settingsTitle"), "Help", panel));
    }

    // ---- First-run card on the Connect page ----

    private void BuildFirstRunCard()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = IconTile("Help", 34, 18);
        tile.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(tile);

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = T("firstRunTitle"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Txt(T("firstRunBody"), "RDTextMuted"));
        var open = new Button { Style = St("RDPrimaryButton"), Content = T("firstRunOpen"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        open.Click += (_, _) => { App.AppSettings.SetTutorialSeen(true); RefreshFirstRunCard(); StartTour(DefaultTutorialPath()); };
        text.Children.Add(open);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var close = new Button { Style = St("RDIconButton"), Content = Ico("Close", 16), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(close, T("dismiss"));
        ToolTipService.SetToolTip(close, T("dismiss"));
        close.Click += (_, _) => { App.AppSettings.SetTutorialSeen(true); RefreshFirstRunCard(); };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        FirstRunCard.Child = grid;
        RefreshFirstRunCard();
    }

    private void RefreshFirstRunCard()
    {
        var show = !App.AppSettings.TutorialSeen && !System.Linq.Enumerable.Any(App.Session.TrustedDevices.GetAll());
        FirstRunCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Page ----

    private void BuildTutorial()
    {
        _tutBuilt = true;
        _tutPath ??= DefaultTutorialPath();
        TutorialContent.Children.Clear();
        _tutRadios.Clear();

        var head = new StackPanel { Spacing = 6 };
        head.Children.Add(new TextBlock { Text = T("title"), Style = St("RDTextTitle"), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock { Text = T("subtitle"), FontSize = (double)Application.Current.Resources["RDTypeSub"], Foreground = Brush("RDTextMutedBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left });
        TutorialContent.Children.Add(head);

        var chooser = new StackPanel { Spacing = 6 };
        var seg = new Border { Style = St("RDSegmentedContainer"), HorizontalAlignment = HorizontalAlignment.Left };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (key, glyph, label) in new[] { ("wifi", "Wifi", "pathWifi"), ("internet", "Globe", "pathInternet"), ("mobile", "Signal", "pathMobile") })
        {
            var rb = new RadioButton { Style = St("RDSegmentedItem"), GroupName = "tutpath", Tag = key, IsChecked = !_tutAfter && _tutPath == key, Content = SegContent(glyph, T(label)) };
            AutomationProperties.SetName(rb, T(label));
            rb.Checked += (_, _) =>
            {
                if (_tutBusy) return;
                _tutPath = key; _tutAfter = false; _tutIndex = 0;
                RenderTutorial(true);
            };
            _tutRadios.Add(rb);
            row.Children.Add(rb);
        }
        seg.Child = row;
        chooser.Children.Add(seg);
        var already = new Button { Style = St("RDGhostButton"), Content = T("alreadyPaired"), HorizontalAlignment = HorizontalAlignment.Left };
        already.Click += (_, _) =>
        {
            _tutAfter = true; _tutIndex = 0;
            SetTutRadios();
            RenderTutorial(true);
        };
        chooser.Children.Add(already);
        var startTour = new Button { Style = St("RDPrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 40 };
        var startContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        startContent.Children.Add(new TextBlock { Text = T("tourStart"), VerticalAlignment = VerticalAlignment.Center });
        startContent.Children.Add(new RDIcon { Glyph = "Chevron", Size = 14, Directional = true, VerticalAlignment = VerticalAlignment.Center });
        startTour.Content = startContent;
        AutomationProperties.SetName(startTour, T("tourStart"));
        startTour.Click += (_, _) => StartTour(_tutAfter ? null : _tutPath, _tutAfter);
        chooser.Children.Add(startTour);
        TutorialContent.Children.Add(chooser);

        // Body: step list (wide column or expander) and the detail column.
        _tutBody.Children.Clear();
        _tutBody.ColumnDefinitions.Clear(); _tutBody.RowDefinitions.Clear();
        _tutBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        _tutBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tutBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _tutBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _tutExpander.Header = T("allSteps");
        _tutExpander.IsExpanded = false;
        _tutExpander.Content = null;
        _tutListSlot.Content = null;
        Grid.SetColumnSpan(_tutExpander, 2);
        _tutBody.Children.Add(_tutExpander);

        var colStack = new StackPanel { Spacing = 8 };
        colStack.Children.Add(new TextBlock { Text = T("allSteps"), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brush("RDTextMutedBrush"), Margin = new Thickness(8, 0, 0, 0) });
        colStack.Children.Add(_tutListSlot);
        _tutColumn.Child = colStack;
        Grid.SetRowSpan(_tutColumn, 2);
        _tutBody.Children.Add(_tutColumn);

        _tutDetail.Children.Clear();
        var progress = new StackPanel { Spacing = 8 };
        _tutProgressLabel.Foreground = Brush("RDTextMutedBrush");
        progress.Children.Add(_tutProgressLabel);
        progress.Children.Add(_tutProgress);
        _tutDetail.Children.Add(progress);
        _tutDetail.Children.Add(_tutCard);
        _tutBody.Children.Add(_tutDetail);
        TutorialContent.Children.Add(_tutBody);

        RenderTutorial(false);
    }

    private static StackPanel SegContent(string glyph, string label)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new RDIcon { Glyph = glyph, Size = 16, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private void SetTutRadios()
    {
        _tutBusy = true;
        foreach (var rb in _tutRadios) rb.IsChecked = !_tutAfter && (string?)rb.Tag == _tutPath;
        _tutBusy = false;
    }

    /// <summary>Wide (page 900 or more): a 260 step list column. Narrower: the list collapses into an expander. Cards stack when the detail column is narrow.</summary>
    private void LayoutTutorial()
    {
        if (!_tutBuilt || _tutBody.RowDefinitions.Count == 0) return;
        var wide = ActualWidth >= 900;
        if (wide)
        {
            if (ReferenceEquals(_tutExpander.Content, _tutListPanel)) _tutExpander.Content = null;
            if (!ReferenceEquals(_tutListSlot.Content, _tutListPanel)) _tutListSlot.Content = _tutListPanel;
        }
        else
        {
            if (ReferenceEquals(_tutListSlot.Content, _tutListPanel)) _tutListSlot.Content = null;
            if (!ReferenceEquals(_tutExpander.Content, _tutListPanel)) _tutExpander.Content = _tutListPanel;
        }
        _tutExpander.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _tutColumn.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _tutBody.ColumnDefinitions[0].Width = new GridLength(wide ? 260 : 0);
        _tutBody.ColumnSpacing = wide ? 16 : 0;
        _tutBody.RowSpacing = wide ? 0 : 12;
        Grid.SetColumn(_tutDetail, wide ? 1 : 0);
        Grid.SetColumnSpan(_tutDetail, wide ? 1 : 2);
        Grid.SetRow(_tutDetail, wide ? 0 : 1);
        Grid.SetRowSpan(_tutDetail, wide ? 2 : 1);

        if (_tutCards is not null && _tutOtherCard is not null)
        {
            var width = double.IsNaN(TutorialContent.Width) ? 1000 : TutorialContent.Width;
            var detail = wide ? width - 276 : width;
            var side = detail - 36 >= 560;
            _tutCards.ColumnDefinitions[1].Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _tutCards.ColumnSpacing = side ? 12 : 0;
            _tutCards.RowSpacing = side ? 0 : 12;
            Grid.SetColumn(_tutOtherCard, side ? 1 : 0);
            Grid.SetRow(_tutOtherCard, side ? 0 : 1);
        }
    }

    // ---- Step rendering ----

    private void RenderTutorial(bool animate)
    {
        var steps = TutSteps();
        _tutStepCount = steps.Count;
        _tutIndex = Math.Clamp(_tutIndex, 0, steps.Count - 1);
        var step = steps[_tutIndex];

        _tutProgressLabel.Text = string.Format(T("stepOf"), N(_tutIndex + 1), N(steps.Count));
        _tutProgress.Value = (_tutIndex + 1) * 100.0 / steps.Count;

        BuildStepList(steps);
        BuildStepCard(steps, step);
        LayoutTutorial();
        if (animate) AnimateStepCard();
    }

    private void BuildStepList(List<TutStep> steps)
    {
        _tutListPanel.Children.Clear();
        for (var i = 0; i < steps.Count; i++)
        {
            var index = i;
            var done = i < _tutIndex;
            var current = i == _tutIndex;
            var circle = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
                Background = Brush(done || current ? "RDIconTileBrush" : "RDPanelBrush"),
                BorderBrush = Brush(done || current ? "RDIconTileLineBrush" : "RDLineBrush"),
                BorderThickness = new Thickness(1),
            };
            circle.Child = done
                ? Ico("Check", 14, "RDAccentBrush")
                : new TextBlock { Text = N(i + 1), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(current ? "RDAccentBrush" : "RDTextMutedBrush") };

            var title = T(steps[i].Id + ".title");
            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.Children.Add(circle);
            var label = new TextBlock { Text = title, FontSize = 13, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Foreground = Brush(current ? "RDTextBrush" : "RDTextMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            rowGrid.Children.Add(label);

            var btn = new Button { Style = St("RDGhostButton"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6), MinHeight = 40, Content = rowGrid };
            if (current) btn.Background = Brush("RDNavSelectedBrush");
            AutomationProperties.SetName(btn, N(i + 1) + ". " + title);
            AutomationProperties.SetItemStatus(btn, current ? Strings.Get("nav.selected") : "");
            btn.Click += (_, _) => { _tutIndex = index; RenderTutorial(true); };
            _tutListPanel.Children.Add(btn);
        }
    }

    private void BuildStepCard(List<TutStep> steps, TutStep step)
    {
        var card = new StackPanel { Spacing = 14 };

        var title = new TextBlock { Text = T(step.Id + ".title"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(title, string.Format(T("a11yStep"), N(_tutIndex + 1), N(steps.Count), T(step.Id + ".title")));
        title.Loaded += (_, _) => FrameworkElementAutomationPeer.FromElement(title)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        card.Children.Add(title);

        if (step.Fork) card.Children.Add(BuildForkChooser());

        _tutCards = new Grid();
        _tutCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tutCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tutCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _tutCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var mine = TutDeviceCard(true, "Monitor", T("thisDevice"), T(step.Id + ".pc"), step.Show);
        _tutOtherCard = TutDeviceCard(false, "Smartphone", T("otherDevice"), T(step.Id + ".phone"), null);
        _tutCards.Children.Add(mine);
        _tutCards.Children.Add(_tutOtherCard);
        card.Children.Add(_tutCards);

        if (step.Link is { } link)
        {
            var btn = new Button { Style = St("RDGhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Content = T(link == "mobile" ? "openMobile" : "openInternet") };
            btn.Click += (_, _) =>
            {
                _tutPath = link; _tutAfter = false; _tutIndex = 0;
                SetTutRadios();
                RenderTutorial(true);
            };
            card.Children.Add(btn);
        }

        var last = _tutIndex >= steps.Count - 1;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var back = new Button { Style = St("RDSecondaryButton"), Content = T("back"), IsEnabled = _tutIndex > 0 };
        back.Click += (_, _) => { if (_tutIndex > 0) { _tutIndex--; RenderTutorial(true); } };
        var next = new Button { Style = St("RDPrimaryButton"), Content = T(last ? "done" : "next") };
        next.Click += (_, _) =>
        {
            if (!last) { _tutIndex++; RenderTutorial(true); return; }
            App.AppSettings.SetTutorialSeen(true);
            RefreshFirstRunCard();
            Navigate("connection");
        };
        footer.Children.Add(back);
        footer.Children.Add(next);
        card.Children.Add(footer);

        _tutCard.Child = card;
    }

    private UIElement BuildForkChooser()
    {
        var internet = !_tutAfter && _tutPath == "internet";
        var options = internet
            ? new[] { ("code", "methodCode"), ("qr", "methodQr") }
            : new[] { ("qr", "methodQr"), ("nearby", "methodNearby") };
        var value = internet ? (_tutFork == "code" ? "code" : "qr") : (_tutFork == "nearby" ? "nearby" : "qr");
        var seg = new Border { Style = St("RDSegmentedContainer"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(12), Padding = new Thickness(3) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (key, label) in options)
        {
            var rb = new RadioButton { Style = St("RDSegmentedItem"), GroupName = "tutfork", MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13, IsChecked = key == value, Content = T(label) };
            rb.Checked += (_, _) =>
            {
                if (_tutFork == key) return;
                _tutFork = key;
                RenderTutorial(false);
            };
            row.Children.Add(rb);
        }
        seg.Child = row;
        return seg;
    }

    private Border TutDeviceCard(bool thisDevice, string glyph, string header, string body, string? show)
    {
        var stack = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(IconTile(glyph, 34, 18));
        head.Children.Add(new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brush("RDTextMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = body, FontSize = 14, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        if (thisDevice && show is not null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = T("showMe"), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new RDIcon { Glyph = "Chevron", Size = 14, Directional = true, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Style = St("RDTonalButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 40 };
            AutomationProperties.SetName(btn, T("showMe"));
            btn.Click += async (_, _) => await ShowTutorialTargetAsync(show);
            stack.Children.Add(btn);
        }
        var border = new Border { Style = St("RDCard"), Padding = new Thickness(16, 14, 16, 14), VerticalAlignment = VerticalAlignment.Top, Child = stack };
        border.BorderBrush = Brush(thisDevice ? "RDAccentTintLineBrush" : "RDLineBrush");
        return border;
    }

    /// <summary>Crossfade 200 ms with an 8 px slide in the reading direction (mirrored in RTL). Instant under reduced motion.</summary>
    private void AnimateStepCard()
    {
        if (!RDMotion.Enabled) return;
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        var move = new TranslateTransform { X = rtl ? -8 : 8 };
        _tutCard.RenderTransform = move;
        _tutCard.Opacity = 0;
        var sb = new Storyboard();
        sb.Children.Add(Ease(_tutCard, "Opacity", 1));
        sb.Children.Add(Ease(move, "X", 0));
        sb.Begin();
    }

    // ---- Show me ----

    private (string Page, FrameworkElement? Target) TutTarget(string key) => key switch
    {
        "nav:connection" => ("connection", ConnectionItem),
        "nav:devices" => ("devices", DevicesItem),
        "remote" => ("connection", RemoteToggle),
        "copyid" => ("connection", CopyIdButton),
        "addphone" => ("connection", AddPhoneButton),
        "qr" => ("connection", QrCard),
        "relay" => ("settings", _inetRelay),
        _ => ("connection", null),
    };

    private async Task ShowTutorialTargetAsync(string key)
    {
        var (page, target) = TutTarget(key);
        Navigate(page);
        await Task.Delay(200);
        if (target is null || target.XamlRoot is null) return;
        target.UpdateLayout();
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await Task.Delay(120);
        Highlight(target);
        if (target is Control c && c.IsTabStop) c.Focus(FocusState.Programmatic);
    }

    /// <summary>2 px accent ring around the target, pulsing twice over 1.2 s (static for 2 s under reduced motion).</summary>
    private async void Highlight(FrameworkElement target)
    {
        var token = ++_highlightToken;
        HighlightLayer.Children.Clear();
        if (target.Visibility != Visibility.Visible || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        var origin = target.TransformToVisual(HighlightLayer).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var ring = new Microsoft.UI.Xaml.Controls.Border
        {
            Width = target.ActualWidth + 8, Height = target.ActualHeight + 8, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2),
            BorderBrush = Brush("RDAccentBrush"), IsHitTestVisible = false,
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ring, origin.X - 4);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(ring, origin.Y - 4);
        HighlightLayer.Children.Add(ring);
        if (RDMotion.Enabled)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            foreach (var (ms, v) in new[] { (0, 1.0), (300, 0.25), (600, 1.0), (900, 0.25), (1200, 1.0) })
                a.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Value = v });
            Storyboard.SetTarget(a, ring);
            Storyboard.SetTargetProperty(a, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(a);
            sb.Begin();
            await Task.Delay(1500);
        }
        else await Task.Delay(2000);
        if (token == _highlightToken) HighlightLayer.Children.Clear();
    }
}

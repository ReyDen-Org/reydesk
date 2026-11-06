using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using SIPSorcery.Net;

namespace ReyDesk.Windows;

public enum RemoteServerState { NotConfigured, Registering, NoId, Connecting, Online, Offline }

/// <summary>
/// What the Connection screen and the status pills draw for the internet side (Desk ID, Key ID, server state, path, remote allowed).
/// No defaults are invented: until the identity/remote backend calls <see cref="Update"/>, the card shows its "not set up" state.
/// The backend raises state through <see cref="Update"/>; the UI raises requests through the events below.
/// </summary>
internal sealed class RemoteUiState
{
    public static RemoteUiState Current { get; } = new();

    public string? DeskId { get; private set; }
    public string? KeyId { get; private set; }
    public RemoteServerState Server { get; private set; } = RemoteServerState.NotConfigured;
    public string? ServerDetail { get; private set; }
    public RDPathKind? Path { get; private set; }
    public bool RemoteAllowed { get; private set; }
    public string? RemoteCode { get; private set; }
    public DateTimeOffset? CodeExpiresAt { get; private set; }

    public event Action? Changed;
    public event Action? RetryRequested;
    public event Action? RegenerateCodeRequested;
    public event Action? CancelCodeRequested;

    public void Update(Action<Editor> edit)
    {
        var e = new Editor(this);
        edit(e);
        Changed?.Invoke();
    }

    public void RequestRetry() => RetryRequested?.Invoke();
    public void RequestRegenerateCode() => RegenerateCodeRequested?.Invoke();
    public void RequestCancelCode() => CancelCodeRequested?.Invoke();

    /// <summary>Text and brush key for the sidebar and title bar pill: Online green, Connecting amber, Offline grey, Off faint.</summary>
    public (string Text, string BrushKey) PillState()
    {
        if (!RemoteAllowed) return (ConnectionStrings.Get("srvOff"), "RDTextDisabledBrush");
        return Server switch
        {
            RemoteServerState.Online => (ConnectionStrings.Get("srvOnline"), "RDSuccessBrush"),
            RemoteServerState.Registering or RemoteServerState.Connecting => (ConnectionStrings.Get("srvConnecting"), "RDWarningBrush"),
            RemoteServerState.NotConfigured => (ConnectionStrings.Get("srvNotSet"), "RDTextFaintBrush"),
            _ => (ConnectionStrings.Get("srvOffline"), "RDTextFaintBrush"),
        };
    }

    public readonly struct Editor
    {
        private readonly RemoteUiState _s;
        public Editor(RemoteUiState s) => _s = s;
        public string? DeskId { set => _s.DeskId = value; }
        public string? KeyId { set => _s.KeyId = value; }
        public RemoteServerState Server { set => _s.Server = value; }
        public string? ServerDetail { set => _s.ServerDetail = value; }
        public RDPathKind? Path { set => _s.Path = value; }
        public bool RemoteAllowed { set => _s.RemoteAllowed = value; }
        public string? RemoteCode { set => _s.RemoteCode = value; }
        public DateTimeOffset? CodeExpiresAt { set => _s.CodeExpiresAt = value; }
    }
}

/// <summary>Shell chrome and the Connection view (D3). Reads RemoteUiState; session state comes from App.Session.</summary>
public sealed partial class MainPage
{
    private bool _connInit;
    private bool _syncingToggle;
    private bool _codePanelOpen;
    private bool _entered;
    private static bool s_enteredOnce;
    private readonly DispatcherTimer _codeTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_connInit) return;
        _connInit = true;
        RemoteUiState.Current.Changed += OnRemoteChanged;
        App.Session.SessionStateChanged += OnHeroSessionChanged;
        App.Session.TrustedDevices.Changed += OnHeroDevicesChanged;
        _codeTimer.Tick += (_, _) => RefreshCodePanel();
        ConnectionSubtitle.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ApplyConnectionLocalization());
        ConnectedDeviceName.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateHero());
        Unloaded += (_, _) =>
        {
            RemoteUiState.Current.Changed -= OnRemoteChanged;
            App.Session.SessionStateChanged -= OnHeroSessionChanged;
            App.Session.TrustedDevices.Changed -= OnHeroDevicesChanged;
            _codeTimer.Stop();
        };
        ApplyShellMetrics(ActualWidth);
        ApplyConnectionLocalization();
        RefreshRemoteCard();
        UpdateHero();
        PlayEntrance();
    }

    private void ApplyShellMetrics(double width)
    {
        if (Sidebar is null || width <= 0) return;
        var show = width >= 760;
        var side = show ? 216 : 0;
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(side);
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // 1px line on the inner edge: BorderThickness is physical, so flip it for RTL.
        Sidebar.BorderThickness = FlowDirection == FlowDirection.RightToLeft ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        ContentHost.Width = Math.Max(0, width - side);
        CompactMenuButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        var pad = width < 760 ? 16 : 28;
        Workspace.Padding = new Thickness(pad, show ? 20 : 8, pad, 20);
        var tutorialWidth = Math.Max(0, ContentHost.Width - pad * 2 - 16);
        TutorialContent.Width = Math.Min(tutorialWidth, 1000);
        LayoutTutorial();
    }

    private void ConnectionContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < 760;
        HeroRing.Compact = compact;
        ConnectedRing.Compact = compact;
    }

    // ---- Nav hover (the inner surface paints the states) ----
    private static (Grid Surface, Rectangle Rail, TextBlock Label)? NavParts(object sender)
    {
        if (sender is not Button { Content: Grid surface }) return null;
        var rail = surface.Children.OfType<Rectangle>().FirstOrDefault();
        var label = surface.Children.OfType<TextBlock>().FirstOrDefault();
        return rail is null || label is null ? null : (surface, rail, label);
    }

    private void Nav_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (NavParts(sender) is not { } p || p.Rail.Visibility == Visibility.Visible) return;
        p.Surface.Background = RDMotion.BrushOf("RDNavHoverBrush");
        p.Label.Foreground = RDMotion.BrushOf("RDTextBrush");
    }

    private void Nav_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (NavParts(sender) is not { } p || p.Rail.Visibility == Visibility.Visible) return;
        p.Surface.Background = RDMotion.BrushOf("RDSidebarBrush");
        p.Label.Foreground = RDMotion.BrushOf("RDTextMutedBrush");
    }

    // ---- Localization of the D3 elements ----
    private void ApplyConnectionLocalization()
    {
        DeskIdLabel.Text = ConnectionStrings.Get("deskIdLabel");
        CopyIdLabel.Text = ConnectionStrings.Get("copy");
        RetryIdButton.Content = ConnectionStrings.Get("retry");
        InternetLabel.Text = ConnectionStrings.Get("internet");
        AddPhoneLabel.Text = ConnectionStrings.Get("addPhone");
        StepNum1.Text = Digits.Localize("1"); StepNum2.Text = Digits.Localize("2"); StepNum3.Text = Digits.Localize("3");
        StepText1.Text = ConnectionStrings.Get("step1");
        StepText2.Text = ConnectionStrings.Get("step2");
        StepText3.Text = ConnectionStrings.Get("step3");
        NearbyHintText.Text = ConnectionStrings.Get("nearbyHint");
        DisconnectAllCardButton.Content = ConnectionStrings.Get("disconnectAll");
        CodeTitleText.Text = ConnectionStrings.Get("codeTitle");
        CodeCancelButton.Content = Strings.Get("common.cancel");
        CodeRegenerateButton.Content = Strings.Get("connection.regenerate");
        SidebarRemotePill.Visibility = Visibility.Visible;
        RefreshRemoteCard();
        UpdateHero();
    }

    // ---- Remote state to UI ----
    private void OnRemoteChanged() => DispatcherQueue.PostSafe(() => { RefreshRemoteCard(); UpdateHero(); });

    private void RefreshRemoteCard()
    {
        var s = RemoteUiState.Current;
        var (pillText, pillBrush) = s.PillState();
        SidebarRemoteText.Text = pillText;
        SidebarRemoteDot.Fill = RDMotion.BrushOf(pillBrush);

        _syncingToggle = true;
        RemoteToggle.IsOn = s.RemoteAllowed;
        _syncingToggle = false;

        var hasId = !string.IsNullOrWhiteSpace(s.DeskId);
        var registering = s.Server == RemoteServerState.Registering && !hasId;
        DeskIdSkeleton.Visibility = registering ? Visibility.Visible : Visibility.Collapsed;
        DeskIdRuns.Visibility = hasId ? Visibility.Visible : Visibility.Collapsed;
        CopyIdButton.Visibility = hasId ? Visibility.Visible : Visibility.Collapsed;
        KeyIdText.Visibility = hasId && !string.IsNullOrWhiteSpace(s.KeyId) ? Visibility.Visible : Visibility.Collapsed;
        var showEmpty = !hasId && !registering;
        DeskIdEmptyText.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        DeskIdGettingText.Text = ConnectionStrings.Get("idGetting");
        DeskIdEmptyText.TextWrapping = TextWrapping.Wrap;
        DeskIdEmptyText.Text = ConnectionStrings.Get(!s.RemoteAllowed ? "idOff" : s.Server switch
        {
            RemoteServerState.NoId => "idNone",
            RemoteServerState.Offline => "idUnreachable",
            _ => "idNotSet",
        });
        RetryIdButton.Visibility = showEmpty && s.RemoteAllowed && s.Server is RemoteServerState.NoId or RemoteServerState.Offline ? Visibility.Visible : Visibility.Collapsed;
        if (hasId)
        {
            var id = new string(s.DeskId!.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var runs = new[] { DeskIdRun0, DeskIdRun1, DeskIdRun2 };
            for (var i = 0; i < 3; i++)
            {
                var start = i * 4;
                var chunk = start >= id.Length ? "" : (i == 2 ? id[start..] : id.Substring(start, Math.Min(4, id.Length - start)));
                runs[i].Text = i < 2 && id.Length > start + 4 ? chunk + "-" : chunk;
                runs[i].Visibility = runs[i].Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            KeyIdText.Text = "KEY " + s.KeyId;
        }

        InternetStateText.Text = !s.RemoteAllowed ? ConnectionStrings.Get("srvOff") : s.Server switch
        {
            RemoteServerState.Online => ConnectionStrings.Get("srvOnline"),
            RemoteServerState.Registering or RemoteServerState.Connecting => ConnectionStrings.Get("srvConnecting"),
            RemoteServerState.NotConfigured => ConnectionStrings.Get("srvNotSet"),
            _ => ConnectionStrings.Get("srvOffline") + (string.IsNullOrWhiteSpace(s.ServerDetail) ? "" : " · " + s.ServerDetail),
        };
        if (s.RemoteAllowed && s.Server == RemoteServerState.Online && s.Path is { } path)
        {
            DeskPathBadge.Kind = path;
            DeskPathBadge.Text = PathText(path);
            DeskPathBadge.Visibility = Visibility.Visible;
        }
        else DeskPathBadge.Visibility = Visibility.Collapsed;

        RefreshCodePanel();
    }

    private static string PathText(RDPathKind kind) => ConnectionStrings.Get(kind switch
    {
        RDPathKind.Lan => "pathLan",
        RDPathKind.Direct => "pathDirect",
        _ => "pathRelay",
    });

    private void RefreshCodePanel()
    {
        RemoteCodePanel.Visibility = _codePanelOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!_codePanelOpen) { _codeTimer.Stop(); return; }
        var s = RemoteUiState.Current;
        var hasCode = s.RemoteAllowed && !string.IsNullOrWhiteSpace(s.RemoteCode);
        var online = s.RemoteAllowed && s.Server == RemoteServerState.Online;
        CodeNoteText.Visibility = !hasCode && !_generatingCode ? Visibility.Visible : Visibility.Collapsed;
        CodeNoteText.Text = ConnectionStrings.Get(!s.RemoteAllowed ? "codeOffNote" : !online ? "codeWaiting" : "codeExpired");
        CodeSkeleton.Visibility = !hasCode && _generatingCode ? Visibility.Visible : Visibility.Collapsed;
        CodeTile.Visibility = CodeExpiryText.Visibility = hasCode ? Visibility.Visible : Visibility.Collapsed;
        CodeRegenerateButton.IsEnabled = online && !_generatingCode;
        CodeDeskIdText.Visibility = !string.IsNullOrWhiteSpace(s.DeskId) ? Visibility.Visible : Visibility.Collapsed;
        CodeDeskIdText.Text = string.IsNullOrWhiteSpace(s.DeskId) ? "" : ReyDesk.Windows.Identity.DeskId.Display(s.DeskId);
        if (!hasCode) { _codeTimer.Stop(); return; }
        var raw = s.RemoteCode!.Trim();
        CodeText.Text = raw.Length == 6 ? raw[..3] + " " + raw[3..] : raw;
        if (s.CodeExpiresAt is { } expires)
        {
            var left = expires - DateTimeOffset.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            CodeExpiryText.Text = string.Format(ConnectionStrings.Get("codeLeft"), Digits.Localize($"{(int)left.TotalMinutes}:{left.Seconds:D2}"));
            _codeTimer.Start();
        }
        else { CodeExpiryText.Text = ""; _codeTimer.Stop(); }
    }

    // ---- Ring hero from live sessions ----
    private void OnHeroSessionChanged(string deviceId) => DispatcherQueue.PostSafe(UpdateHero);
    private void OnHeroDevicesChanged() => DispatcherQueue.PostSafe(UpdateHero);

    private void UpdateHero()
    {
        var devices = App.Session.TrustedDevices.GetAll();
        var states = devices.Select(d => App.Session.TryGetSessionState(d.DeviceId)).ToArray();
        var connected = states.Count(x => x == RTCPeerConnectionState.connected);
        var connecting = states.Any(x => x is RTCPeerConnectionState.connecting or RTCPeerConnectionState.@new);
        var remote = RemoteUiState.Current.Path is RDPathKind.Direct or RDPathKind.Relay;

        HeroRing.State = connecting ? RDRingState.Connecting : RDRingState.Idle;
        HeroRing.StatusText = ConnectionStrings.Get(connecting ? "connectingLine" : "idleLine");

        ConnectedRing.State = remote ? RDRingState.ConnectedRemote : RDRingState.ConnectedLocal;
        ConnectedRing.StatusText = ConnectedDeviceName.Text;
        var kind = RemoteUiState.Current.Path ?? RDPathKind.Lan;
        ConnectedPathBadge.Kind = kind;
        ConnectedPathBadge.Text = PathText(kind);
        ConnectedCountText.Text = string.Format(Strings.Get("connection.connectedCount"), Digits.Localize(connected));
    }

    // ---- Handlers ----
    private void RemoteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;
        App.Session.Remote.SetRemoteAllowed(RemoteToggle.IsOn);
    }

    private void AddPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        _codePanelOpen = !_codePanelOpen;
        RefreshCodePanel();
        if (_codePanelOpen && string.IsNullOrEmpty(RemoteUiState.Current.RemoteCode)) _ = GenerateCodeAsync();
    }

    private void CodeCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _codePanelOpen = false;
        _codeWanted = false;
        RemoteUiState.Current.RequestCancelCode();
        RefreshCodePanel();
    }

    private void CodeRegenerateButton_Click(object sender, RoutedEventArgs e) => RemoteUiState.Current.RequestRegenerateCode();
    private void RetryIdButton_Click(object sender, RoutedEventArgs e) => RemoteUiState.Current.RequestRetry();

    private async void CopyIdButton_Click(object sender, RoutedEventArgs e)
    {
        var id = RemoteUiState.Current.DeskId;
        if (string.IsNullOrWhiteSpace(id)) return;
        CopyText(ReyDesk.Windows.Identity.DeskId.Display(id));
        ShowToast(RDToastKind.Success, ConnectionStrings.Get("copied"));
        CopyIdIcon.Glyph = "Check";
        CopyIdLabel.Text = ConnectionStrings.Get("copied");
        await Task.Delay(1600);
        CopyIdIcon.Glyph = "Copy";
        CopyIdLabel.Text = ConnectionStrings.Get("copy");
    }

    // RDMotion.To reuses one shared KeySpline instance, which WinUI rejects on the second keyframe, so build a fresh spline per animation here.
    private static Timeline Ease(DependencyObject target, string property, double to)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(RDMotion.Base), Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new(0.22, 1), ControlPoint2 = new(0.36, 1) },
        });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }

    // ---- First-load entrance: opacity + 6px rise, 200ms, 30ms stagger, once per app run, skipped under reduced motion ----
    private void PlayEntrance()
    {
        if (_entered || s_enteredOnce || !RDMotion.Enabled) return;
        _entered = s_enteredOnce = true;
        var sb = new Storyboard();
        var i = 0;
        foreach (var el in new UIElement[] { HeroRing, DeskIdCard, AddPhoneButton, QrCard })
        {
            var tt = new TranslateTransform { Y = 6 };
            el.RenderTransform = tt;
            el.Opacity = 0;
            var fade = Ease(el, "Opacity", 1);
            var rise = Ease(tt, "Y", 0);
            fade.BeginTime = rise.BeginTime = TimeSpan.FromMilliseconds(30 * i++);
            sb.Children.Add(fade);
            sb.Children.Add(rise);
        }
        sb.Begin();
    }
}

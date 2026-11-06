using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using ReyDesk.Windows.Controls;
using Rect = global::Windows.Foundation.Rect;
using Size = global::Windows.Foundation.Size;
using VirtualKey = global::Windows.System.VirtualKey;

namespace ReyDesk.Windows;

/// <summary>
/// Guided tour: dims the live app, cuts out the control a step points at (four dim rectangles so the cut-out
/// stays clickable), rings it with a pulsing accent border and shows a small card with the step text. Reuses TutSteps() and the
/// "Show me" resolver (TutTarget) from MainPage.Tutorial.cs. Any lookup failure falls back to a centred card.
/// </summary>
public sealed partial class MainPage
{
    private const double TourPad = 6, TourMargin = 12, TourCardMax = 340, TourGap = 12;

    private bool _tourActive;
    private int _tourToken;
    private FrameworkElement? _tourTarget;
    private Rect? _tourRect;
    private Rect? _tourShown;
    private bool _tourBuilt;
    private Button? _tourNext;
    private Storyboard? _tourPulse;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tourAnim;
    private readonly Rectangle[] _tourDim = new Rectangle[4];
    private readonly Border _tourRing = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _tourCardBox = new() { Style = (Style)Application.Current.Resources["RDCard"], Padding = new Thickness(16, 14, 16, 14) };

    // ---- Entry ----

    /// <summary>Start the tour at step 1. A path picks that guide; "after" starts the "already paired" steps.</summary>
    private async void StartTour(string? path, bool after = false)
    {
        try
        {
            if (path is not null) _tutPath = path; else _tutPath ??= DefaultTutorialPath();
            _tutAfter = after; _tutIndex = 0; _tutFork = "qr";
            EnsureTourBuilt();
            _tourActive = true;
            TourOverlay.Visibility = Visibility.Visible;
            ShellGrid.KeyDown -= Tour_KeyDown;
            ShellGrid.KeyDown += Tour_KeyDown;
            await ShowTourStepAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Guided tour start failed: " + ex);
            EndTour(false);
        }
    }

    private void EnsureTourBuilt()
    {
        if (_tourBuilt) return;
        _tourBuilt = true;
        var dimBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(184, 0, 0, 0));
        for (var i = 0; i < 4; i++)
        {
            _tourDim[i] = new Rectangle { Fill = dimBrush, IsHitTestVisible = true };
            TourCanvas.Children.Add(_tourDim[i]);
        }
        _tourRing.BorderBrush = Brush("RDAccentBrush");
        TourCanvas.Children.Add(_tourRing);
        _tourCardBox.RenderTransform = new TranslateTransform();
        TourCanvas.Children.Add(_tourCardBox);
    }

    private void EndTour(bool done)
    {
        _tourActive = false;
        _tourToken++;
        DetachTourTarget();
        _tourAnim?.Stop();
        _tourPulse?.Stop();
        ShellGrid.KeyDown -= Tour_KeyDown;
        TourOverlay.Visibility = Visibility.Collapsed;
        _tourRing.Visibility = Visibility.Collapsed;
        _tourShown = null; _tourRect = null;
        App.AppSettings.SetTutorialSeen(true);
        RefreshFirstRunCard();
        if (done) Navigate("connection");
    }

    private void Tour_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tourActive && e.Key == VirtualKey.Escape) { e.Handled = true; EndTour(false); }
    }

    private void TourOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_tourActive) LayoutTour(false);
    }

    // ---- Step ----

    private async Task ShowTourStepAsync()
    {
        var token = ++_tourToken;
        DetachTourTarget();
        var steps = TutSteps();
        _tutIndex = Math.Clamp(_tutIndex, 0, steps.Count - 1);
        var step = steps[_tutIndex];
        _tutStepCount = steps.Count;
        BuildTourCard(steps, step);
        _tourRect = null;
        LayoutTour(false, hideCard: true);

        FrameworkElement? target = null;
        if (step.Show is { } key)
        {
            try
            {
                var (page, el) = TutTarget(key);
                Navigate(page);
                if (el is not null)
                {
                    for (var waited = 0; waited <= 2500; waited += 50)
                    {
                        if (token != _tourToken) return;
                        if (TourTargetReady(el)) { target = el; break; }
                        await Task.Delay(50);
                    }
                    if (target is not null)
                    {
                        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                        await Task.Delay(120);
                        if (token != _tourToken) return;
                        if (!TourTargetReady(target)) target = null;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Guided tour target lookup failed: " + ex);
                target = null;
            }
        }
        if (token != _tourToken || !_tourActive) return;

        _tourTarget = target;
        if (target is not null) target.LayoutUpdated += Tour_TargetLayoutUpdated;
        LayoutTour(true);
        AnimateTourCard();
        _tourNext?.Focus(FocusState.Programmatic);
    }

    private bool TourTargetReady(FrameworkElement el)
    {
        if (el.XamlRoot is null || !el.IsLoaded || el.Visibility != Visibility.Visible) return false;
        if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return false;
        return TourBounds(el) is not null;
    }

    private Rect? TourBounds(FrameworkElement el)
    {
        try
        {
            var p = el.TransformToVisual(TourOverlay).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var w = el.ActualWidth; var h = el.ActualHeight;
            // In RTL the visual left edge of the element is its transformed point minus its width; TransformBounds handles both.
            var b = el.TransformToVisual(TourOverlay).TransformBounds(new Rect(0, 0, w, h));
            if (b.Width <= 0 || b.Height <= 0 || double.IsNaN(b.X) || double.IsNaN(b.Y)) return null;
            if (b.Right < 0 || b.Bottom < 0 || b.X > TourOverlay.ActualWidth || b.Y > TourOverlay.ActualHeight) return null;
            _ = p;
            return new Rect(b.X - TourPad, b.Y - TourPad, b.Width + TourPad * 2, b.Height + TourPad * 2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Guided tour bounds failed: " + ex.Message);
            return null;
        }
    }

    private void DetachTourTarget()
    {
        if (_tourTarget is not null) _tourTarget.LayoutUpdated -= Tour_TargetLayoutUpdated;
        _tourTarget = null;
    }

    private void Tour_TargetLayoutUpdated(object? sender, object e)
    {
        if (!_tourActive || _tourTarget is null || _tourRect is not { } old) return;
        var now = TourBounds(_tourTarget);
        if (now is not { } r) return;
        if (Math.Abs(r.X - old.X) < 0.5 && Math.Abs(r.Y - old.Y) < 0.5 && Math.Abs(r.Width - old.Width) < 0.5 && Math.Abs(r.Height - old.Height) < 0.5) return;
        LayoutTour(false);
    }

    // ---- Card ----

    private void BuildTourCard(List<TutStep> steps, TutStep step)
    {
        var stack = new StackPanel { Spacing = 12 };
        var title = T(step.Id + ".title");
        var label = string.Format(T("stepOf"), N(_tutIndex + 1), N(steps.Count));

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brush("RDAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Style = St("RDIconButton"), Content = Ico("Close", 16), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(close, T("tourSkip"));
        ToolTipService.SetToolTip(close, T("tourSkip"));
        close.Click += (_, _) => EndTour(false);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        if (step.Fork) stack.Children.Add(BuildTourFork());
        stack.Children.Add(TourBlock(T("thisDevice"), T(step.Id + ".pc"), true));
        stack.Children.Add(TourBlock(T("otherDevice"), T(step.Id + ".phone"), false));

        if (step.Link is { } link)
        {
            var btn = new Button { Style = St("RDGhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Content = T(link == "mobile" ? "openMobile" : "openInternet") };
            btn.Click += async (_, _) =>
            {
                _tutPath = link; _tutAfter = false; _tutIndex = 0; _tutFork = "qr";
                await ShowTourStepAsync();
            };
            stack.Children.Add(btn);
        }

        var last = _tutIndex >= steps.Count - 1;
        var footer = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = new Button { Style = St("RDSecondaryButton"), Content = T("back"), IsEnabled = _tutIndex > 0 };
        back.Click += async (_, _) => { if (_tutIndex > 0) { _tutIndex--; await ShowTourStepAsync(); } };
        var skip = new Button { Style = St("RDGhostButton"), Content = T("tourSkip") };
        skip.Click += (_, _) => EndTour(false);
        var next = new Button { Style = St("RDPrimaryButton"), Content = T(last ? "done" : "next") };
        next.Click += async (_, _) =>
        {
            if (last) { EndTour(true); return; }
            _tutIndex++;
            await ShowTourStepAsync();
        };
        _tourNext = next;
        Grid.SetColumn(skip, 1);
        skip.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(next, 2);
        footer.Children.Add(back); footer.Children.Add(skip); footer.Children.Add(next);
        stack.Children.Add(footer);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, Content = stack };
        _tourCardBox.FlowDirection = FlowDirection;
        _tourCardBox.Child = scroll;
        AutomationProperties.SetName(_tourCardBox, string.Format(T("a11yStep"), N(_tutIndex + 1), N(steps.Count), title));
        AutomationProperties.SetLiveSetting(_tourCardBox, AutomationLiveSetting.Polite);
    }

    private Border TourBlock(string header, string body, bool mine)
    {
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brush(mine ? "RDAccentBrush" : "RDTextMutedBrush") });
        s.Children.Add(new TextBlock { Text = body, FontSize = 13, Foreground = Brush(mine ? "RDTextBrush" : "RDTextMutedBrush"), TextWrapping = TextWrapping.Wrap });
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = s };
        if (mine) { b.Background = Brush("RDAccentTintBrush"); b.BorderBrush = Brush("RDAccentTintLineBrush"); b.BorderThickness = new Thickness(1); }
        return b;
    }

    private UIElement BuildTourFork()
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
            var rb = new RadioButton { Style = St("RDSegmentedItem"), GroupName = "tourfork", MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13, IsChecked = key == value, Content = T(label) };
            rb.Checked += async (_, _) =>
            {
                if (_tutFork == key) return;
                _tutFork = key;
                await ShowTourStepAsync();
            };
            row.Children.Add(rb);
        }
        seg.Child = row;
        return seg;
    }

    // ---- Layout ----

    private void LayoutTour(bool animate, bool hideCard = false)
    {
        try
        {
            var w = TourOverlay.ActualWidth; var h = TourOverlay.ActualHeight;
            if (w <= 0 || h <= 0) return;
            Rect? target = _tourTarget is not null ? TourBounds(_tourTarget) : null;
            _tourRect = target;
            PlaceTourCard(w, h, target);
            if (hideCard) _tourCardBox.Opacity = 0;

            if (target is not { } t)
            {
                _tourAnim?.Stop();
                _tourPulse?.Stop();
                _tourShown = null;
                ApplyCutout(null, w, h);
                return;
            }
            if (animate && RDMotion.Enabled && _tourShown is { } from) AnimateCutout(from, t, w, h);
            else { _tourAnim?.Stop(); _tourShown = t; ApplyCutout(t, w, h); }
            StartTourPulse();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Guided tour layout failed: " + ex);
            try { _tourTarget = null; _tourRect = null; PlaceTourCard(TourOverlay.ActualWidth, TourOverlay.ActualHeight, null); ApplyCutout(null, TourOverlay.ActualWidth, TourOverlay.ActualHeight); }
            catch (Exception inner) { System.Diagnostics.Debug.WriteLine("Guided tour fallback failed: " + inner.Message); }
        }
    }

    private void ApplyCutout(Rect? cut, double w, double h)
    {
        if (cut is not { } r)
        {
            SetRect(_tourDim[0], 0, 0, w, h);
            for (var i = 1; i < 4; i++) SetRect(_tourDim[i], 0, 0, 0, 0);
            _tourRing.Visibility = Visibility.Collapsed;
            return;
        }
        var x = Math.Max(0, r.X); var y = Math.Max(0, r.Y);
        var right = Math.Min(w, r.Right); var bottom = Math.Min(h, r.Bottom);
        SetRect(_tourDim[0], 0, 0, w, y);
        SetRect(_tourDim[1], 0, bottom, w, h - bottom);
        SetRect(_tourDim[2], 0, y, x, bottom - y);
        SetRect(_tourDim[3], right, y, w - right, bottom - y);
        _tourRing.Width = Math.Max(0, right - x); _tourRing.Height = Math.Max(0, bottom - y);
        Canvas.SetLeft(_tourRing, x); Canvas.SetTop(_tourRing, y);
        _tourRing.Visibility = Visibility.Visible;
    }

    private static void SetRect(Rectangle rect, double x, double y, double w, double h)
    {
        rect.Width = Math.Max(0, w); rect.Height = Math.Max(0, h);
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
    }

    /// <summary>200 ms ease-out move of the cut-out from the previous rect to the next one.</summary>
    private void AnimateCutout(Rect from, Rect to, double w, double h)
    {
        _tourAnim?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _tourAnim = timer;
        var start = Environment.TickCount64;
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (Environment.TickCount64 - start) / (double)RDMotion.Base);
            var e = 1 - Math.Pow(1 - t, 3);
            var r = new Rect(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e, from.Width + (to.Width - from.Width) * e, from.Height + (to.Height - from.Height) * e);
            _tourShown = r;
            ApplyCutout(r, w, h);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void StartTourPulse()
    {
        if (!RDMotion.Enabled) { _tourPulse?.Stop(); _tourRing.Opacity = 1; return; }
        if (_tourPulse is not null) return;
        var a = new DoubleAnimation { From = 0.5, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(600)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, _tourRing);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        _tourPulse = sb;
        sb.Begin();
    }

    /// <summary>Below the target if it fits, else above, else centred; clamped inside the window with 12 px margins.</summary>
    private void PlaceTourCard(double w, double h, Rect? target)
    {
        var width = Math.Min(TourCardMax, w - 24);
        _tourCardBox.Width = width;
        if (_tourCardBox.Child is ScrollViewer sv) sv.MaxHeight = Math.Max(120, h - 2 * TourMargin - 32);
        _tourCardBox.Measure(new Size(width, Math.Max(120, h - 2 * TourMargin)));
        var ch = Math.Min(_tourCardBox.DesiredSize.Height, h - 2 * TourMargin);
        double x, y;
        if (target is { } t)
        {
            x = t.X + t.Width / 2 - width / 2;
            if (t.Bottom + TourGap + ch + TourMargin <= h) y = t.Bottom + TourGap;
            else if (t.Y - TourGap - ch >= TourMargin) y = t.Y - TourGap - ch;
            else { x = (w - width) / 2; y = (h - ch) / 2; }
        }
        else { x = (w - width) / 2; y = (h - ch) / 2; }
        x = Math.Clamp(x, TourMargin, Math.Max(TourMargin, w - width - TourMargin));
        y = Math.Clamp(y, TourMargin, Math.Max(TourMargin, h - ch - TourMargin));
        Canvas.SetLeft(_tourCardBox, x); Canvas.SetTop(_tourCardBox, y);
    }

    /// <summary>160 ms fade plus 8 px slide in; instant under reduced motion.</summary>
    private void AnimateTourCard()
    {
        _tourCardBox.Opacity = 1;
        if (!RDMotion.Enabled || _tourCardBox.RenderTransform is not TranslateTransform move) return;
        move.Y = 8;
        _tourCardBox.Opacity = 0;
        var fade = new DoubleAnimation { To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(160)) };
        Storyboard.SetTarget(fade, _tourCardBox); Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(160)) };
        Storyboard.SetTarget(slide, move); Storyboard.SetTargetProperty(slide, "Y");
        var sb = new Storyboard();
        sb.Children.Add(fade); sb.Children.Add(slide);
        sb.Begin();
    }
}

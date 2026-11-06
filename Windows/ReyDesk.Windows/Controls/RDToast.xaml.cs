using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace ReyDesk.Windows.Controls;

public enum RDToastKind { Info, Success, Warning, Error }

/// <summary>
/// Toast (2.16). Place one instance at the bottom of a page (defaults to bottom-center). Show(...) enters in 200ms (opacity + translateY 8),
/// auto-hides after 4s, exit 120ms. Errors stay until closed. Optional ghost action button.
/// </summary>
public sealed partial class RDToast : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Action? _action;
    private Storyboard? _running;
    private bool _shown;

    public RDToast()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Hide();
    }

    public void Show(RDToastKind kind, string text, string? actionLabel = null, Action? action = null)
    {
        _timer.Stop();
        _action = action;
        Message.Text = text;
        AutomationProperties.SetLiveSetting(Message, kind == RDToastKind.Error ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var (glyph, brush) = kind switch
        {
            RDToastKind.Success => ("Check", "RDSuccessBrush"),
            RDToastKind.Warning => ("Warning", "RDWarningBrush"),
            RDToastKind.Error => ("Warning", "RDDangerBrush"),
            _ => ("Info", "RDAccentBrush"),
        };
        Glyph.Glyph = glyph;
        Glyph.Foreground = RDMotion.BrushOf(brush);
        ActionButton.Visibility = string.IsNullOrEmpty(actionLabel) || action is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = actionLabel;
        CloseButton.Visibility = kind == RDToastKind.Error ? Visibility.Visible : Visibility.Collapsed;

        Visibility = Visibility.Visible;
        if (!_shown)
        {
            Slide.Y = 8;
            Root.Opacity = 0;
        }
        _shown = true;
        Run(1, 0, RDMotion.Base, null);
        if (kind != RDToastKind.Error) _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        if (!_shown) return;
        _shown = false;
        Run(0, 0, RDMotion.Fast, () => { if (!_shown) Visibility = Visibility.Collapsed; });
    }

    private void Run(double opacity, double y, int ms, Action? done)
    {
        _running?.Stop();
        var sb = new Storyboard();
        sb.Children.Add(RDMotion.To(Root, "Opacity", opacity, ms));
        sb.Children.Add(RDMotion.To(Slide, "Y", y, ms));
        sb.Completed += (_, _) => { Root.Opacity = opacity; Slide.Y = y; done?.Invoke(); };
        _running = sb;
        sb.Begin();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var a = _action;
        Hide();
        a?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}

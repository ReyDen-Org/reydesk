using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ReyDesk.Windows.Controls;

/// <summary>
/// .rf-skeleton block: RDSkeletonBrush, radius 6 by default, shimmer to RDSkeletonHiBrush and back over 1.4s.
/// Reduced motion = static hover colour. Size it with Width/Height (copy the final layout).
/// </summary>
public sealed class RDSkeleton : UserControl
{
    private readonly SolidColorBrush _brush = new();
    private readonly Border _block = new() { CornerRadius = new CornerRadius(6) };
    private Storyboard? _loop;

    public RDSkeleton()
    {
        IsTabStop = false;
        Height = 16;
        _block.Background = _brush;
        Content = _block;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => { _loop?.Stop(); _loop = null; };
    }

    public CornerRadius Radius { get => _block.CornerRadius; set => _block.CornerRadius = value; }

    private void Start()
    {
        _loop?.Stop();
        var lo = RDMotion.ColorOf("RDcInk3");
        var hi = RDMotion.ColorOf("RDcInk4");
        if (!RDMotion.Enabled) { _brush.Color = hi; return; }
        _brush.Color = lo;
        var a = new ColorAnimation { From = lo, To = hi, Duration = TimeSpan.FromMilliseconds(700), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, _brush);
        Storyboard.SetTargetProperty(a, "Color");
        _loop = new Storyboard();
        _loop.Children.Add(a);
        _loop.Begin();
    }
}

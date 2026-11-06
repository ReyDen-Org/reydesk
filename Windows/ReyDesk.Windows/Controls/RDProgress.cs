using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ReyDesk.Windows.Controls;

/// <summary>
/// .rf-progress: 8 high pill, track RDHoverBrush, fill gradient accent fill to accent. Value 0..100.
/// Indeterminate = 30% bar sliding 1.4s; static 30% bar when motion is reduced.
/// </summary>
public sealed class RDProgress : UserControl
{
    private readonly Border _track = new() { Height = 8, CornerRadius = new CornerRadius(4), Background = RDMotion.BrushOf("RDHoverBrush") };
    private readonly Grid _clip = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _fill = new() { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _move = new();
    private Storyboard? _loop;
    private double _value;
    private bool _indeterminate;

    public RDProgress()
    {
        IsTabStop = false;
        _fill.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0.5),
            EndPoint = new global::Windows.Foundation.Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Color = RDMotion.ColorOf("RDcAccentFill"), Offset = 0 },
                new GradientStop { Color = RDMotion.ColorOf("RDcAccentHover"), Offset = 1 },
            },
        };
        _fill.RenderTransform = _move;
        _clip.Children.Add(_track);
        _clip.Children.Add(_fill);
        _track.HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = _clip;
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
        Unloaded += (_, _) => _loop?.Stop();
    }

    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Layout(); } }

    public bool IsIndeterminate { get => _indeterminate; set { _indeterminate = value; Layout(); } }

    private void Layout()
    {
        _loop?.Stop();
        _loop = null;
        _move.X = 0;
        var w = ActualWidth;
        if (w <= 0) return;
        _clip.Clip = new RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, w, 8) };
        if (!_indeterminate)
        {
            _fill.Width = w * _value / 100.0;
            return;
        }
        _fill.Width = w * 0.3;
        if (!RDMotion.Enabled) return;
        var a = new DoubleAnimation { From = -w * 0.3, To = w, Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, _move);
        Storyboard.SetTargetProperty(a, "X");
        _loop = new Storyboard();
        _loop.Children.Add(a);
        _loop.Begin();
    }
}

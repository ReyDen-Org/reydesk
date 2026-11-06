using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ReyDesk.Windows.Controls;

/// <summary>
/// Stroke icon on a 24 grid (Lucide paths in Theme/Icons.xaml, key "RDIcon" + Glyph), stroke 1.9, round caps and joins.
/// Colour = Foreground (inherits from the parent button). Directional glyphs mirror in RTL when Directional is true.
/// </summary>
public sealed class RDIcon : UserControl
{
    private readonly Path _path = new()
    {
        StrokeThickness = 1.9,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };
    private readonly Viewbox _box;
    private string _glyph = "";
    private double _size = 20;
    private bool _directional;

    public RDIcon()
    {
        IsTabStop = false;
        var host = new Grid { Width = 24, Height = 24 };
        host.Children.Add(_path);
        _box = new Viewbox { Child = host, Stretch = Stretch.Uniform, RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5) };
        Content = _box;
        Width = Height = _size;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Apply());
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => Apply());
        Loaded += (_, _) => Apply();
    }

    /// <summary>Icon name without the "RDIcon" prefix, e.g. "Copy".</summary>
    public string Glyph { get => _glyph; set { _glyph = value ?? ""; Apply(); } }

    public double Size { get => _size; set { _size = value; Width = Height = value; } }

    public bool Directional { get => _directional; set { _directional = value; Apply(); } }

    private void Apply()
    {
        _path.Stroke = Foreground;
        _box.RenderTransform = _directional && FlowDirection == FlowDirection.RightToLeft ? new ScaleTransform { ScaleX = -1 } : null;
        if (_glyph.Length == 0) { _path.Data = null; return; }
        if (Application.Current.Resources.TryGetValue("RDIcon" + _glyph, out var raw) && raw is string s)
            _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), s);
    }
}

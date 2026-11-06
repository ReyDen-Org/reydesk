using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ReyDesk.Windows.Controls;

public enum RDRingState { Idle, Connecting, ConnectedLocal, ConnectedRemote, Error }

/// <summary>
/// Status ring hero (2.3). Status only (no hover). Glow box 232, disc 176 (Compact: 200 / 152), state line with dot under the ring.
/// State changes crossfade over 320ms; connecting arc turns 1 per 1.3s; connected glow breathes .5 to .95 over 2.2s. Loops stop under reduced motion.
/// Badges (LAN / Direct / Relay) are placed by the caller next to or under the control.
/// </summary>
public sealed partial class RDStatusRing : UserControl
{
    private RDRingState _state = RDRingState.Idle;
    private string _text = "";
    private bool _compact;
    private readonly SolidColorBrush _edge = new();
    private Storyboard? _loops;
    private Storyboard? _fade;

    public RDStatusRing()
    {
        InitializeComponent();
        Edge.Stroke = _edge;
        Loaded += (_, _) => { SizeIt(); Apply(false); };
        Unloaded += (_, _) => { _loops?.Stop(); };
    }

    public RDRingState State { get => _state; set { _state = value; if (IsLoaded) Apply(true); } }

    public string StatusText { get => _text; set { _text = value ?? ""; Line.Text = _text; } }

    public bool Compact { get => _compact; set { _compact = value; if (IsLoaded) { SizeIt(); Apply(false); } } }

    private void SizeIt()
    {
        double box = _compact ? 200 : 232, d = _compact ? 152 : 176;
        Box.Width = Box.Height = box;
        Disc.Width = Disc.Height = Track.Width = Track.Height = Edge.Width = Edge.Height = ArcHost.Width = ArcHost.Height = d;
        var r = d / 2 - 1.25;
        var c = d / 2;
        var end = new global::Windows.Foundation.Point(c + r * Math.Cos(Math.PI / 6), c + r * Math.Sin(Math.PI / 6));
        var fig = new PathFigure { StartPoint = new global::Windows.Foundation.Point(c, c - r) };
        fig.Segments.Add(new ArcSegment { Point = end, Size = new global::Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = false });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        Arc.Data = geo;
        Spin.CenterX = Spin.CenterY = c;
    }

    private void Apply(bool animate)
    {
        _loops?.Stop();
        _fade?.Stop();
        Line.Text = _text;

        double glowA = 0, glowD = 0, track = 0, arc = 0, edgeOp = 1, thick = 2.5;
        string edgeKey = "RDcAccent", dotKey = "RDTextFaintBrush", icon = "Devices", iconBrush = "RDTextMutedBrush";
        switch (_state)
        {
            case RDRingState.Idle: glowA = .30; edgeKey = "RDcLineStrong"; thick = 1.5; break;
            case RDRingState.Connecting: glowA = .45; track = 1; arc = 1; edgeOp = 0; dotKey = "RDWarningBrush"; iconBrush = "RDAccentBrush"; break;
            case RDRingState.ConnectedLocal: glowA = .72; dotKey = "RDSuccessBrush"; icon = "PhoneCheck"; iconBrush = "RDAccentBrush"; break;
            case RDRingState.ConnectedRemote: glowA = .72; dotKey = "RDSuccessBrush"; icon = "Globe"; iconBrush = "RDAccentBrush"; break;
            case RDRingState.Error: glowD = .55; edgeKey = "RDcDanger"; thick = 2; dotKey = "RDDangerBrush"; icon = "Warning"; iconBrush = "RDDangerBrush"; break;
        }
        Edge.StrokeThickness = thick;
        Center.Glyph = icon;
        Center.Foreground = RDMotion.BrushOf(iconBrush);
        Dot.Fill = RDMotion.BrushOf(dotKey);
        var edgeColor = RDMotion.ColorOf(edgeKey);

        var sb = new Storyboard();
        int ms = animate ? RDMotion.Slow : 0;
        sb.Children.Add(RDMotion.To(GlowAccent, "Opacity", glowA, ms));
        sb.Children.Add(RDMotion.To(GlowDanger, "Opacity", glowD, ms));
        sb.Children.Add(RDMotion.To(Track, "Opacity", track, ms));
        sb.Children.Add(RDMotion.To(ArcHost, "Opacity", arc, ms));
        sb.Children.Add(RDMotion.To(Edge, "Opacity", edgeOp, ms));
        if (animate && RDMotion.Enabled)
        {
            var ca = new ColorAnimation { To = edgeColor, Duration = TimeSpan.FromMilliseconds(RDMotion.Slow) };
            Storyboard.SetTarget(ca, _edge);
            Storyboard.SetTargetProperty(ca, "Color");
            sb.Children.Add(ca);
        }
        else _edge.Color = edgeColor;
        _fade = sb;
        sb.Begin();

        // Loops: connecting arc, connected breathing. None when motion is reduced (static frame above).
        if (!RDMotion.Enabled) return;
        _loops = new Storyboard();
        if (_state == RDRingState.Connecting)
        {
            var spin = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1300), RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(spin, Spin);
            Storyboard.SetTargetProperty(spin, "Angle");
            _loops.Children.Add(spin);
        }
        else if (_state is RDRingState.ConnectedLocal or RDRingState.ConnectedRemote)
        {
            var breathe = new DoubleAnimation
            {
                From = .5, To = .95, Duration = TimeSpan.FromMilliseconds(1100), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(RDMotion.Slow), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(breathe, GlowAccent);
            Storyboard.SetTargetProperty(breathe, "Opacity");
            _loops.Children.Add(breathe);
        }
        if (_loops.Children.Count > 0) _loops.Begin();
    }
}

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ReyDesk.Windows.Controls;

public enum RDPathKind { Lan, Direct, Relay }

/// <summary>Connection path pill (2.6). Text is supplied localized by the caller; add a tooltip on the control for the explanation.</summary>
public sealed partial class RDPathBadge : UserControl
{
    private RDPathKind _kind = RDPathKind.Lan;
    private string _text = "";

    public RDPathBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
    }

    public RDPathKind Kind { get => _kind; set { _kind = value; Apply(); } }

    public string Text { get => _text; set { _text = value ?? ""; Apply(); } }

    private void Apply()
    {
        var (bg, line, fg, glyph) = _kind switch
        {
            RDPathKind.Lan => ("RDAccentTintBrush", "RDAccentTintLineBrush", "RDAccentBrush", "Router"),
            RDPathKind.Direct => (null, "RDAccentBrush", "RDAccentBrush", "ArrowLink"),
            _ => ("RDWarningBgBrush", "RDWarningLineBrush", "RDWarningBrush", "Server"),
        };
        Pill.Fill = bg is null ? null : RDMotion.BrushOf(bg);
        Pill.Stroke = RDMotion.BrushOf(line);
        var fgBrush = RDMotion.BrushOf(fg);
        Label.Foreground = fgBrush;
        Glyph.Foreground = fgBrush;
        Glyph.Glyph = glyph;
        Label.Text = _text.Length > 0 ? _text : _kind.ToString();
    }
}

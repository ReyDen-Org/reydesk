using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace ReyDesk.Windows.Controls;

/// <summary>Motion tokens (contract 2.12) and the reduced-motion switch every looping control reads.</summary>
public static class RDMotion
{
    /// <summary>Debug override used by the control gallery to preview reduced motion.</summary>
    public static bool ForceReduced { get; set; }

    private static readonly global::Windows.UI.ViewManagement.UISettings Ui = new();

    /// <summary>False when the OS animation setting is off (or the gallery forces it): loops stop, durations are 0.</summary>
    public static bool Enabled => !ForceReduced && Ui.AnimationsEnabled;

    public const int Fast = 120, Base = 200, Slow = 320;

    public static Duration Ms(int ms) => new(Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero);

    public static KeySpline EaseOut => new() { ControlPoint1 = new(0.22, 1), ControlPoint2 = new(0.36, 1) };

    /// <summary>Resolve a token colour (RDc*) from the app resources.</summary>
    public static Color ColorOf(string key) => (Color)Application.Current.Resources[key];

    public static Brush BrushOf(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Animate a double property with the ease-out spline.</summary>
    public static Timeline To(DependencyObject target, string property, double to, int ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero, Value = to, KeySpline = EaseOut });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }
}

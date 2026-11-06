using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Settings;

namespace ReyDesk.Windows;

/// <summary>Shared builders for the code-built panels (D4): contract card anatomy, icon tiles, chips, toggle rows,
/// dialogs and the one toast host. Only RD* keys and RDIcon glyph names are used here.</summary>
public sealed partial class MainPage
{
    private const string Dash = "-";
    private RDToast _toast = null!;

    private static Style St(string key) => (Style)Application.Current.Resources[key];
    private static string N(string text) => Digits.Localize(text);
    private static string N(long value) => Digits.Localize(value);

    /// <summary>Date and time in the app language (not the OS culture), 24h; Persian calendar for FA. Digits localized.</summary>
    private static string FormatLocalDateTime(DateTimeOffset value)
    {
        var (name, pattern) = Strings.Current switch
        {
            AppLanguage.Persian => ("fa-IR", "yyyy/MM/dd HH:mm"),
            _ => ("en-US", "yyyy-MM-dd HH:mm"),
        };
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo(name).Clone();
        string text;
        try
        {
            if (Strings.Current == AppLanguage.Persian) culture.DateTimeFormat.Calendar = new System.Globalization.PersianCalendar();
            text = value.ToString(pattern, culture);
        }
        catch (ArgumentException) { text = value.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture); }
        return N(text);
    }

    private void InitializeToast()
    {
        _toast = new RDToast { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 20) };
        Grid.SetColumn(_toast, 1);
        ShellGrid.Children.Add(_toast);
    }

    private void ShowToast(RDToastKind kind, string text, string? actionLabel = null, Action? action = null) => _toast?.Show(kind, text, actionLabel, action);

    private RDIcon Ico(string glyph, double size, string brushKey = "RDTextMutedBrush") =>
        new() { Glyph = glyph, Size = size, Foreground = Brush(brushKey), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private Border IconTile(string glyph, double box = 28, double glyphSize = 16, string brushKey = "RDAccentBrush") => new()
    {
        Width = box, Height = box, CornerRadius = new CornerRadius(box >= 40 ? 11 : 9), VerticalAlignment = VerticalAlignment.Center,
        Background = Brush("RDIconTileBrush"), BorderBrush = Brush("RDIconTileLineBrush"), BorderThickness = new Thickness(1),
        Child = Ico(glyph, glyphSize, brushKey)
    };

    /// <summary>Button content for "Add device": stroke Plus icon + label (any legacy "+ " prefix in the string is dropped).</summary>
    private StackPanel AddDeviceContent()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ico("Plus", 18, "RDOnAccentBrush"));
        row.Children.Add(new TextBlock { Text = Strings.Get("devices.add").TrimStart('+', ' '), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static TextBlock Txt(string text, string styleKey = "RDTextBody") => new() { Text = text, Style = St(styleKey) };

    private static TextBlock Mono(string text, bool selectable = false) =>
        new() { Text = text, Style = St("RDTextMono"), FlowDirection = FlowDirection.LeftToRight, IsTextSelectionEnabled = selectable, TextWrapping = TextWrapping.Wrap };

    /// <summary>Opaque pill with a status dot (no translucent brush on a rounded border).</summary>
    private Border Chip(string text, string dotBrushKey)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brush(dotBrushKey), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brush("RDTextMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = Brush("RDPanelBrush"), BorderBrush = Brush("RDLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Height = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = row };
    }

    private Border CardBox(UIElement content, Thickness? padding = null) =>
        new() { Style = St("RDCard"), Padding = padding ?? new Thickness(16, 14, 16, 14), Child = content };

    private StackPanel CardHeader(string glyph, string title)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(IconTile(glyph));
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    /// <summary>Contract card: icon tile 28 plus 15 SemiBold title, content below.</summary>
    private Border BuildCard(string title, string glyph, UIElement content)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(CardHeader(glyph, title));
        panel.Children.Add(content);
        return CardBox(panel);
    }

    private void AddCard(string titleKey, string glyph, UIElement content) => SettingsSectionsHost.Children.Add(BuildCard(Strings.Get(titleKey), glyph, content));

    private Grid SettingRow(string key, FrameworkElement control)
    {
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Txt(Strings.Get(key), "RDTextLabel"));
        Grid.SetRow(control, 1); grid.Children.Add(control); SetAccessibleName(control, key);
        return grid;
    }

    private static ToggleSwitch NewToggle(bool on) => new() { OnContent = "", OffContent = "", IsOn = on, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Tile 28, label with optional helper, toggle at the reading end.</summary>
    private Grid ToggleRow(string glyph, string label, string? helper, ToggleSwitch toggle)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(IconTile(glyph));
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(helper)) text.Children.Add(Txt(helper, "RDTextHelper"));
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        return row;
    }

    /// <summary>Status row with a dot, used by Diagnostics and Internet.</summary>
    private Grid StatusRow(string label, string value, string dotBrushKey)
    {
        var row = new Grid { ColumnSpacing = 10, MinHeight = 28 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(dotBrushKey), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 1 };
        if (!string.IsNullOrEmpty(label)) text.Children.Add(Txt(label, "RDTextLabel"));
        text.Children.Add(new TextBlock { Text = value, FontSize = 13, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }

    private ContentDialog Dlg(string title, object content, string? primary, string close, bool danger = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = content is string s ? Txt(s, "RDTextMuted") : content,
            CloseButtonText = close, DefaultButton = ContentDialogButton.None,
            RequestedTheme = ElementTheme.Dark, CloseButtonStyle = St("RDSecondaryButton")
        };
        if (primary is not null) { dialog.PrimaryButtonText = primary; dialog.PrimaryButtonStyle = St(danger ? "RDDangerButton" : "RDPrimaryButton"); }
        // No default button (it would restyle that button as accent). Decline/Cancel takes focus so Enter never approves.
        dialog.Opened += (d, _) => FocusDialogClose(d);
        return dialog;
    }

    private static void FocusDialogClose(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "CloseButton" } b) { b.Focus(FocusState.Programmatic); return; }
            FocusDialogClose(child);
        }
    }

    /// <summary>Lets the older feedback call sites (Severity, Message, IsOpen) raise a toast instead of a persistent InfoBar.</summary>
    private sealed class ToastFeedback
    {
        private readonly MainPage _page;
        public ToastFeedback(MainPage page) => _page = page;
        public InfoBarSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public bool IsOpen { set { if (value) _page.ShowToast(Severity == InfoBarSeverity.Success ? RDToastKind.Success : RDToastKind.Error, Message); } }
    }

    private Grid SkeletonCard(double height)
    {
        var host = new Grid();
        var box = new StackPanel { Spacing = 12 };
        box.Children.Add(new RDSkeleton { Height = height * 0.5, Radius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Stretch });
        box.Children.Add(new RDSkeleton { Height = 14, Width = 140, Radius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left });
        box.Children.Add(new RDSkeleton { Height = 12, Width = 90, Radius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
        host.Children.Add(CardBox(box, new Thickness(16)));
        return host;
    }
}

using System.Collections;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace ReyDesk.Windows.Controls;

/// <summary>One option: the label shown and the source object it stands for.</summary>
public sealed record RDListEntry(string Label, object? Item);

/// <summary>
/// Custom listbox (2.9), replaces every ComboBox. Pattern from Forever CustomSelect.tsx: trigger button + popup list under it, same width.
/// Keyboard: Enter/Space/Up/Down open from the trigger; in the list Up/Down/Home/End move, type-ahead jumps, Enter/Space commit,
/// Escape/Tab close; focus always returns to the trigger. Outside click closes (light dismiss, no exit animation).
/// Set ItemsSource (labels from DisplayMemberPath or ToString), then SelectedIndex / SelectedItem. Listen to SelectionChanged.
/// </summary>
public sealed partial class RDListBox : UserControl
{
    private readonly List<RDListEntry> _entries = new();
    private int _index = -1;
    private string _placeholder = "";
    private string _typed = "";
    private DateTime _typedAt = DateTime.MinValue;
    private IEnumerable? _source;

    public RDListBox()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var name = AutomationProperties.GetName(this);
            if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(Trigger, name);
            Refresh();
        };
    }

    public event EventHandler? SelectionChanged;

    public string? DisplayMemberPath { get; set; }

    public string PlaceholderText { get => _placeholder; set { _placeholder = value ?? ""; Refresh(); } }

    public IEnumerable? ItemsSource
    {
        get => _source;
        set
        {
            _source = value;
            _entries.Clear();
            if (value is not null)
                foreach (var o in value) _entries.Add(new RDListEntry(Label(o), o));
            List.ItemsSource = _entries.ToList();
            _index = -1;
            Refresh();
        }
    }

    public int SelectedIndex
    {
        get => _index;
        set { _index = value >= 0 && value < _entries.Count ? value : -1; Refresh(); }
    }

    public object? SelectedItem
    {
        get => _index >= 0 ? _entries[_index].Item : null;
        set => SelectedIndex = _entries.FindIndex(e => Equals(e.Item, value));
    }

    private string Label(object? o)
    {
        if (o is null) return "";
        if (!string.IsNullOrEmpty(DisplayMemberPath) && o.GetType().GetProperty(DisplayMemberPath)?.GetValue(o) is { } v) return v.ToString() ?? "";
        return o.ToString() ?? "";
    }

    private void Refresh()
    {
        if (_index >= 0)
        {
            ValueText.Text = _entries[_index].Label;
            ValueText.Foreground = RDMotion.BrushOf("RDTextBrush");
        }
        else
        {
            ValueText.Text = _placeholder;
            ValueText.Foreground = RDMotion.BrushOf("RDTextFaintBrush");
        }
        List.SelectedIndex = _index;
    }

    // ---- open / close ----

    private void Trigger_Click(object sender, RoutedEventArgs e) => Open();

    private void Trigger_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Down or VirtualKey.Up)
        {
            e.Handled = true;
            Open();
        }
    }

    private void Open()
    {
        if (Pop.IsOpen || _entries.Count == 0 || !IsEnabled) return;
        var w = Trigger.ActualWidth;
        PopRoot.Width = w;
        PopRoot.FlowDirection = FlowDirection;
        PopRoot.Shadow ??= new ThemeShadow();
        PopRoot.Translation = new Vector3(0, 0, 32);
        PopRoot.Measure(new global::Windows.Foundation.Size(w, double.PositiveInfinity));
        var need = PopRoot.DesiredSize.Height;
        var offset = Trigger.ActualHeight + 4;
        if (XamlRoot?.Content is UIElement root)
        {
            var y = Trigger.TransformToVisual(root).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            var below = XamlRoot.Size.Height - (y + Trigger.ActualHeight);
            if (need + 8 > below && y > below) offset = -(need + 4);
        }
        Pop.HorizontalOffset = 0;
        Pop.VerticalOffset = offset;
        PopRoot.Opacity = 0;
        PopSlide.Y = -3;
        Pop.IsOpen = true;
        TurnChevron(180);
        var sb = new Storyboard();
        sb.Children.Add(RDMotion.To(PopRoot, "Opacity", 1, RDMotion.Fast));
        sb.Children.Add(RDMotion.To(PopSlide, "Y", 0, RDMotion.Fast));
        sb.Completed += (_, _) => { PopRoot.Opacity = 1; PopSlide.Y = 0; };
        sb.Begin();

        List.SelectedIndex = _index;
        List.UpdateLayout();
        var start = _index >= 0 ? _index : 0;
        FocusRow(start);
    }

    private void Close(bool commitFocus = true)
    {
        if (!Pop.IsOpen) return;
        List.SelectedIndex = _index;
        var sb = new Storyboard();
        sb.Children.Add(RDMotion.To(PopRoot, "Opacity", 0, RDMotion.Fast));
        sb.Completed += (_, _) => Pop.IsOpen = false;
        sb.Begin();
        if (commitFocus) Trigger.Focus(FocusState.Keyboard);
    }

    private void Pop_Closed(object? sender, object e)
    {
        TurnChevron(0);
        PopRoot.Opacity = 0;
    }

    private void TurnChevron(double angle)
    {
        var sb = new Storyboard();
        sb.Children.Add(RDMotion.To(ChevronTurn, "Angle", angle, RDMotion.Base));
        sb.Completed += (_, _) => ChevronTurn.Angle = angle;
        sb.Begin();
    }

    // ---- selection and keyboard ----

    private void Commit(int index)
    {
        if (index < 0 || index >= _entries.Count) { Close(); return; }
        var changed = index != _index;
        _index = index;
        Refresh();
        Close();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void List_ItemClick(object sender, ItemClickEventArgs e) => Commit(_entries.IndexOf((RDListEntry)e.ClickedItem));

    private void FocusRow(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        List.SelectedIndex = index;
        List.ScrollIntoView(_entries[index]);
        List.UpdateLayout();
        (List.ContainerFromIndex(index) as ListViewItem)?.Focus(FocusState.Keyboard);
    }

    private int FocusedRow()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is ListViewItem item) return List.IndexFromContainer(item);
        return List.SelectedIndex;
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var cur = Math.Max(FocusedRow(), 0);
        switch (e.Key)
        {
            case VirtualKey.Down: e.Handled = true; FocusRow(Math.Min(cur + 1, _entries.Count - 1)); return;
            case VirtualKey.Up: e.Handled = true; FocusRow(Math.Max(cur - 1, 0)); return;
            case VirtualKey.Home: e.Handled = true; FocusRow(0); return;
            case VirtualKey.End: e.Handled = true; FocusRow(_entries.Count - 1); return;
            case VirtualKey.Enter:
            case VirtualKey.Space:
                e.Handled = true; Commit(FocusedRow()); return;
            case VirtualKey.Escape: e.Handled = true; Close(); return;
            case VirtualKey.Tab: Close(false); return;
        }
        var ch = KeyToChar(e.Key);
        if (ch is null) return;
        e.Handled = true;
        var now = DateTime.UtcNow;
        _typed = (now - _typedAt).TotalMilliseconds > 700 ? ch.Value.ToString() : _typed + ch.Value;
        _typedAt = now;
        var from = _typed.Length == 1 ? cur + 1 : cur;
        for (var i = 0; i < _entries.Count; i++)
        {
            var idx = (from + i) % _entries.Count;
            if (_entries[idx].Label.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase)) { FocusRow(idx); return; }
        }
    }

    private static char? KeyToChar(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return (char)('a' + (k - VirtualKey.A));
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return (char)('0' + (k - VirtualKey.Number0));
        return null;
    }
}

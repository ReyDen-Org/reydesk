using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Windows.Companion;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using Windows.Storage.Pickers;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private bool _approvedActionsAttached;
    private void InitializeApprovedActions()
    {
        Loaded += (_, _) => {
            if (_approvedActionsAttached) return;
            App.Session.ApprovedActionRequested += ConfirmApprovedActionAsync; _approvedActionsAttached = true;
        };
        Unloaded += (_, _) => {
            if (!_approvedActionsAttached) return;
            App.Session.ApprovedActionRequested -= ConfirmApprovedActionAsync; _approvedActionsAttached = false;
        };
    }
    private void AddApprovedActionsSection()
    {
        var service = ApprovedActionService.Instance;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Txt(Strings.Get("actions.help"), "RDTextHelper"));
        var registered = new StackPanel { Spacing = 10 };
        panel.Children.Add(registered);
        var label = new TextBox { Style = St("RDTextBox"), MaxLength = 80, PlaceholderText = Strings.Get("actions.label") };
        var profile = new TextBox { Style = St("RDTextBox"), MaxLength = 48, Text = "General" };
        var kind = NewList(new[] { Strings.Get("actions.application"), Strings.Get("actions.shortcut") }, 0);
        var executable = new TextBox { Style = St("RDTextBox"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var choose = new Button { Content = Strings.Get("actions.chooseExe"), Style = St("RDSecondaryButton") };
        var arguments = new TextBox { Style = St("RDTextBox"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 16400, MinHeight = 80, MaxHeight = 200, PlaceholderText = Strings.Get("actions.argumentsHint") };
        var ctrl = new CheckBox { Style = St("RDCheckBox"), Content = "Ctrl", IsChecked = true };
        var shift = new CheckBox { Style = St("RDCheckBox"), Content = "Shift" }; var alt = new CheckBox { Style = St("RDCheckBox"), Content = "Alt" };
        var letter = NewList(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), 0);
        var shortcut = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        modifiers.Children.Add(ctrl); modifiers.Children.Add(shift); modifiers.Children.Add(alt);
        shortcut.Children.Add(modifiers); shortcut.Children.Add(SettingRow("actions.letter", letter));
        var appFields = new StackPanel { Spacing = 10 };
        appFields.Children.Add(SettingRow("actions.executable", executable)); appFields.Children.Add(choose); appFields.Children.Add(SettingRow("actions.arguments", arguments));
        kind.SelectionChanged += (_, _) => { appFields.Visibility = kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; shortcut.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; };
        var feedback = new ToastFeedback(this);
        string? editing = null;
        var save = new Button { Content = Strings.Get("actions.add"), Style = St("RDPrimaryButton") };
        var reset = new Button { Content = Strings.Get("actions.new"), Style = St("RDSecondaryButton") };
        void Reset()
        {
            editing = null; label.Text = ""; profile.Text = "General"; kind.SelectedIndex = 0;
            executable.Text = ""; arguments.Text = ""; ctrl.IsChecked = true; shift.IsChecked = alt.IsChecked = false; letter.SelectedIndex = 0;
            save.Content = Strings.Get("actions.add");
        }
        void Refresh()
        {
            registered.Children.Clear();
            var actions = service.Snapshot();
            if (actions.Length == 0) registered.Children.Add(Txt(Strings.Get("actions.empty"), "RDTextHelper"));
            foreach (var action in actions.OrderBy(a => a.Profile).ThenBy(a => a.Label))
            {
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(HubText(action.Profile + " · " + action.Label, 15, true));
                row.Children.Add(HubText(action.Shortcut.Length > 0 ? action.Shortcut : action.Executable, 12));
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var edit = new Button { Content = Strings.Get("actions.edit"), Style = St("RDSecondaryButton") };
                edit.Click += (_, _) => {
                    editing = action.Id; label.Text = action.Label; profile.Text = action.Profile;
                    executable.Text = action.Executable; arguments.Text = string.Join(Environment.NewLine, action.Arguments);
                    kind.SelectedIndex = action.Shortcut.Length > 0 ? 1 : 0;
                    var parts = action.Shortcut.Split('+'); ctrl.IsChecked = parts.Contains("Ctrl"); shift.IsChecked = parts.Contains("Shift"); alt.IsChecked = parts.Contains("Alt");
                    letter.SelectedIndex = parts[^1].Length == 1 ? parts[^1][0] - 'A' : 0;
                    save.Content = Strings.Get("actions.save"); feedback.IsOpen = false;
                    label.Focus(FocusState.Programmatic);
                };
                var remove = new Button { Content = Strings.Get("actions.remove"), Style = St("RDSecondaryButton") };
                remove.Click += (_, _) => {
                    var success = service.Remove(action.Id);
                    feedback.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                    feedback.Message = Strings.Get(success ? "actions.removed" : "actions.saveFailed"); feedback.IsOpen = true;
                    if (success) { if (editing == action.Id) Reset(); Refresh(); }
                };
                controls.Children.Add(edit); controls.Children.Add(remove); row.Children.Add(controls);
                registered.Children.Add(HubCard(row));
            }
        }
        choose.Click += async (_, _) => {
            choose.IsEnabled = false;
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is not null) executable.Text = file.Path;
            }
            catch (Exception) { feedback.Severity = InfoBarSeverity.Error; feedback.Message = Strings.Get("transfer.unavailable"); feedback.IsOpen = true; }
            finally { choose.IsEnabled = true; }
        };
        save.Click += (_, _) => {
            var keys = new List<string>();
            if (ctrl.IsChecked == true) keys.Add("Ctrl"); if (shift.IsChecked == true) keys.Add("Shift"); if (alt.IsChecked == true) keys.Add("Alt");
            keys.Add(((char)('A' + Math.Clamp(letter.SelectedIndex, 0, 25))).ToString());
            var chord = kind.SelectedIndex == 1 ? string.Join('+', keys) : "";
            var args = kind.SelectedIndex == 0 && arguments.Text.Length > 0 ? arguments.Text.Replace("\r\n", "\n").Split('\n') : Array.Empty<string>();
            var exe = kind.SelectedIndex == 0 ? executable.Text : "";
            var success = editing is null ? service.Add(label.Text, profile.Text, exe, args, chord) : service.Update(editing, label.Text, profile.Text, exe, args, chord);
            feedback.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            feedback.Message = Strings.Get(success ? "actions.saved" : "actions.saveFailed"); feedback.IsOpen = true;
            if (success) { Reset(); Refresh(); }
        };
        reset.Click += (_, _) => { Reset(); feedback.IsOpen = false; };
        panel.Children.Add(SettingRow("actions.label", label)); panel.Children.Add(SettingRow("actions.profile", profile)); panel.Children.Add(SettingRow("actions.kind", kind)); panel.Children.Add(appFields); panel.Children.Add(shortcut);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; buttons.Children.Add(save); buttons.Children.Add(reset);
        panel.Children.Add(buttons); Refresh();
        AddCard("actions.title", "Shield", panel);
    }
    private void AddRecoveryStatusSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(StatusRow("", Strings.Get("recovery.unconfigured"), "RDTextFaintBrush"));
        panel.Children.Add(Txt(Strings.Get("recovery.explanation"), "RDTextHelper"));
        panel.Children.Add(new Button { Content = Strings.Get("recovery.sendCode"), IsEnabled = false, Style = St("RDSecondaryButton") });
        AddCard("recovery.title", "Key", panel);
    }
    private Task<bool> ConfirmApprovedActionAsync(string deviceId, string sessionId, ApprovedAction action) => OnHubUiAsync(async cancellation => {
        if (!_approvedActionsAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return false;
        await _consentDialog.WaitAsync(cancellation);
        try
        {
            (Application.Current as App)?.ShowMainWindow();
            var device = App.Session.TrustedDevices.GetAll().FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is null) return false;
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(HubText(DeviceLabel(device), 16, true));
            body.Children.Add(HubText(action.Profile + " · " + action.Label, 17, true));
            body.Children.Add(HubText(action.Shortcut.Length > 0 ? action.Shortcut : action.Executable, 13));
            if (action.Shortcut.Length > 0)
            {
                body.Children.Add(HubText(Strings.Get("actions.target") + " " + action.TargetWindowTitle, 13));
                body.Children.Add(HubText(Strings.Get("actions.shortcutWarning"), 13));
            }
            else if (action.Arguments.Length > 0) body.Children.Add(HubText(Strings.Get("actions.arguments") + "\n" + string.Join("\n", action.Arguments), 12));
            var dialog = Dlg(Strings.Get("actions.confirm"), new ScrollViewer { Content = body, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Strings.Get("actions.runOnce"), Strings.Get("common.cancel"));
            using var expiry = cancellation.Register(() => DispatcherQueue.PostSafe(dialog.Hide));
            if (cancellation.IsCancellationRequested) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellation.IsCancellationRequested &&
                _approvedActionsAttached && App.Session.IsCurrentSession(deviceId, sessionId);
        }
        finally { _consentDialog.Release(); }
    }, false);
}

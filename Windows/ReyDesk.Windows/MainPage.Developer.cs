using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReyDesk.Windows.Companion;
using ReyDesk.Windows.Controls;
using ReyDesk.Windows.Localization;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private bool _developerAttached;
    private void InitializeDeveloperMode()
    {
        Loaded += (_, _) => {
            if (_developerAttached) return;
            App.Session.DeveloperTaskRequested += ConfirmDeveloperTaskAsync; _developerAttached = true;
        };
        Unloaded += (_, _) => {
            if (!_developerAttached) return;
            App.Session.DeveloperTaskRequested -= ConfirmDeveloperTaskAsync; _developerAttached = false;
        };
    }
    private void AddDeveloperSection()
    {
        var service = DeveloperTaskService.Instance;
        var panel = new StackPanel { Spacing = 12 };
        var enabled = NewToggle(service.Enabled);
                panel.Children.Add(ToggleRow("Code", Strings.Get("developer.enable"), null, enabled)); panel.Children.Add(Txt(Strings.Get("developer.help"), "RDTextMuted"));
        panel.Children.Add(Txt(Strings.Get("developer.lifetime"), "RDTextHelper"));
        var tasks = new StackPanel { Spacing = 10 };
        panel.Children.Add(tasks);
        var label = new TextBox { Style = St("RDTextBox"), MaxLength = 80 };
        var executable = new TextBox { Style = St("RDTextBox"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var folder = new TextBox { Style = St("RDTextBox"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var exePicker = new Button { Content = Strings.Get("actions.chooseExe"), Style = St("RDSecondaryButton") };
        var folderPicker = new Button { Content = Strings.Get("developer.chooseFolder"), Style = St("RDSecondaryButton") };
        var arguments = new TextBox { Style = St("RDTextBox"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 16400, MinHeight = 80, MaxHeight = 200, PlaceholderText = Strings.Get("actions.argumentsHint") };
        var shareOutput = new CheckBox { Style = St("RDCheckBox"), Content = Strings.Get("developer.shareOutput"), IsChecked = false };
        panel.Children.Add(Txt(Strings.Get("developer.outputWarning"), "RDTextError"));
        void Feedback(string key, bool success)
        {
            ShowToast(success ? RDToastKind.Success : RDToastKind.Error, Strings.Get(key));
        }
        void Refresh()
        {
            tasks.Children.Clear();
            var definitions = service.Snapshot();
            using var live = JsonDocument.Parse(service.ListPublic());
            var statuses = live.RootElement.GetProperty("tasks").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e);
            if (definitions.Length == 0) tasks.Children.Add(Txt(Strings.Get("developer.empty"), "RDTextHelper"));
            foreach (var task in definitions)
            {
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(HubText(task.Label, 15, true));
                row.Children.Add(HubText(task.Executable, 12)); row.Children.Add(HubText(task.WorkingDirectory, 12));
                var hasStatus = statuses.TryGetValue(task.Id, out var state);
                var running = hasStatus && state.GetProperty("running").GetBoolean();
                var status = !service.Enabled ? Strings.Get("developer.disabledStatus") : !hasStatus ? Strings.Get("developer.statusUnavailable") :
                    running ? Strings.Get("developer.running") : state.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ?
                    string.Format(Strings.Get("developer.exited"), exitCode.GetInt32()) : Strings.Get("developer.notStarted");
                row.Children.Add(HubText(status, 13));
                row.Children.Add(HubText(Strings.Get(task.ShareOutput ? "developer.outputShared" : "developer.outputPrivate"), 12));
                if (hasStatus && task.ShareOutput && state.TryGetProperty("output", out var output) && output.GetString() is { Length: > 0 } text)
                {
                    var log = HubText(text, 12); log.IsTextSelectionEnabled = true;
                    row.Children.Add(new ScrollViewer { Content = log, MaxHeight = 180, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
                }
                var remove = new Button { Content = Strings.Get("actions.remove"), IsEnabled = !running, Style = St("RDSecondaryButton") };
                remove.Click += (_, _) => { var success = service.Remove(task.Id); Feedback(success ? "developer.removed" : "developer.removeFailed", success); if (success) Refresh(); };
                row.Children.Add(remove); tasks.Children.Add(HubCard(row));
            }
        }
        var toggling = false;
        enabled.Toggled += async (_, _) => {
            if (toggling) return;
            toggling = true; enabled.IsEnabled = false;
            var before = service.Enabled;
            try
            {
                if (enabled.IsOn)
                {
                    await _consentDialog.WaitAsync();
                    try
                    {
                        var dialog = Dlg(Strings.Get("developer.enable"), Strings.Get("developer.enableBody"), Strings.Get("hub.allow"), Strings.Get("common.cancel"), danger: true);
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { enabled.IsOn = before; return; }
                    }
                    finally { _consentDialog.Release(); }
                }
                if (!service.SetEnabled(enabled.IsOn)) { enabled.IsOn = before; Feedback("developer.saveFailed", false); }
                Refresh();
            }
            catch (Exception) { enabled.IsOn = before; Feedback("developer.saveFailed", false); }
            finally { enabled.IsEnabled = true; toggling = false; }
        };
        exePicker.Click += async (_, _) => {
            exePicker.IsEnabled = false;
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                var file = await picker.PickSingleFileAsync(); if (file is not null) executable.Text = file.Path;
            }
            catch (Exception) { Feedback("transfer.unavailable", false); }
            finally { exePicker.IsEnabled = true; }
        };
        folderPicker.Click += async (_, _) => {
            folderPicker.IsEnabled = false;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                var selected = await picker.PickSingleFolderAsync(); if (selected is not null) folder.Text = selected.Path;
            }
            catch (Exception) { Feedback("transfer.unavailable", false); }
            finally { folderPicker.IsEnabled = true; }
        };
        var add = new Button { Content = Strings.Get("developer.register"), Style = St("RDPrimaryButton") };
        add.Click += (_, _) => {
            var args = arguments.Text.Length > 0 ? arguments.Text.Replace("\r\n", "\n").Split('\n') : Array.Empty<string>();
            var success = service.Add(label.Text, executable.Text, folder.Text, args, shareOutput.IsChecked == true);
            Feedback(success ? "developer.saved" : "developer.saveFailed", success);
            if (success) { label.Text = executable.Text = folder.Text = arguments.Text = ""; shareOutput.IsChecked = false; Refresh(); }
        };
        var refresh = new Button { Content = Strings.Get("diagnostics.refresh"), Style = St("RDSecondaryButton") };
        refresh.Click += (_, _) => Refresh();
        panel.Children.Add(refresh); panel.Children.Add(SettingRow("actions.label", label)); panel.Children.Add(SettingRow("actions.executable", executable)); panel.Children.Add(exePicker);
        panel.Children.Add(SettingRow("developer.folder", folder)); panel.Children.Add(folderPicker); panel.Children.Add(SettingRow("actions.arguments", arguments)); panel.Children.Add(shareOutput);
        panel.Children.Add(add); Refresh();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(CardHeader("Code", Strings.Get("developer.title")));
        var badge = Chip(Strings.Get("developer.badge"), "RDWarningBrush"); badge.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(badge);
        SettingsSectionsHost.Children.Add(CardBox(new Expander { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = false, Content = panel }));
    }
    private Task<bool> ConfirmDeveloperTaskAsync(string deviceId, string sessionId, DeveloperTaskDefinition task, bool stop) => OnHubUiAsync(async cancellation => {
        if (!_developerAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return false;
        await _consentDialog.WaitAsync(cancellation);
        try
        {
            (Application.Current as App)?.ShowMainWindow();
            var device = App.Session.TrustedDevices.GetAll().FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is null) return false;
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(HubText(DeviceLabel(device), 16, true)); body.Children.Add(HubText(task.Label, 18, true));
            body.Children.Add(HubText(task.Executable, 13)); body.Children.Add(HubText(task.WorkingDirectory, 13));
            if (!stop && task.Arguments.Length > 0) body.Children.Add(HubText(Strings.Get("actions.arguments") + "\n" + string.Join("\n", task.Arguments), 12));
            body.Children.Add(HubText(Strings.Get(stop ? "developer.stopWarning" : task.ShareOutput ? "developer.outputWarning" : "developer.outputPrivate"), 13));
            var dialog = Dlg(Strings.Get(stop ? "developer.confirmStop" : "developer.confirmStart"), new ScrollViewer { Content = body, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Strings.Get(stop ? "developer.stopOnce" : "developer.startOnce"), Strings.Get("common.cancel"), danger: stop);
            using var expiry = cancellation.Register(() => DispatcherQueue.PostSafe(dialog.Hide));
            if (cancellation.IsCancellationRequested) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellation.IsCancellationRequested &&
                _developerAttached && App.Session.IsCurrentSession(deviceId, sessionId);
        }
        finally { _consentDialog.Release(); }
    }, false);
}

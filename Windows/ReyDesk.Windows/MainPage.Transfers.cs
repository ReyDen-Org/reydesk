using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;
using ReyDesk.Windows.Controls;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Localization;
using ReyDesk.Windows.Transfer;
using Windows.Storage.Pickers;

namespace ReyDesk.Windows;

public sealed partial class MainPage
{
    private sealed record TransferView(string SessionId, string Name, TransferStatus Status);
    private readonly Dictionary<string, TransferView> _transferViews = new();
    private sealed record TransferChoice(string Tag, string Label) { public override string ToString() => Label; }
    private RDListBox _transferDevices = null!;
    private TextBlock _transferTitle = null!, _transferHelp = null!, _transferName = null!, _transferState = null!, _transferLimits = null!;
    private TextBlock _dropTitle = null!, _dropHint = null!, _folderTitle = null!, _queueTitle = null!;
    private RDProgress _transferProgress = null!;
    private ToastFeedback _transferFeedback = null!;
    private Button _transferSend = null!, _transferCancel = null!;
    private Button _receiveFolderChoose = null!, _receiveFolderForget = null!;
    private TextBlock _receiveFolderStatus = null!, _receiveFolderHelp = null!;
    private Grid _dropZone = null!;
    private Rectangle _dropOutline = null!;
    private Border _queueCard = null!;
    private string? _transferDevice;
    private bool _transfersAttached, _refreshingTransfers, _choosingTransfer, _choosingReceiveFolder;

    private void InitializeTransfers()
    {
        _transferFeedback = new ToastFeedback(this) { Severity = InfoBarSeverity.Error };
        _transferTitle = new TextBlock { Style = St("RDTextTitle") };
        _transferHelp = Txt("", "RDTextMuted");
        _transferDevices = new RDListBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _transferDevices.SelectionChanged += (_, _) => {
            if (_refreshingTransfers) return;
            _transferDevice = (_transferDevices.SelectedItem as TransferChoice)?.Tag;
            RenderTransfer();
        };
        _transferSend = new Button { Style = St("RDPrimaryButton"), HorizontalAlignment = HorizontalAlignment.Center };
        _transferSend.Click += async (_, _) => await PickAndSendFileAsync();
        _transferCancel = new Button { Style = St("RDGhostButton") };
        _transferCancel.Click += (_, _) => {
            if (_transferDevice is null || !_transferViews.TryGetValue(_transferDevice, out var view) ||
                !App.Session.IsCurrentSession(_transferDevice, view.SessionId)) return;
            App.Session.CancelFile(_transferDevice);
            _transferState.Text = Strings.Get("transfer.canceling");
        };

        // Drop zone: dashed outline, folder tile, title, hint, send button.
        _dropOutline = new Rectangle { RadiusX = 16, RadiusY = 16, StrokeThickness = 1.5, StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 4 }, Stroke = Brush("RDLineStrongBrush"), IsHitTestVisible = false };
        var dropBody = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(20, 28, 20, 28) };
        var dropTile = IconTile("Folder", 56, 24); dropTile.HorizontalAlignment = HorizontalAlignment.Center;
        _dropTitle = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brush("RDTextBrush"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _dropHint = Txt("", "RDTextHelper"); _dropHint.TextAlignment = TextAlignment.Center;
        dropBody.Children.Add(dropTile); dropBody.Children.Add(_dropTitle); dropBody.Children.Add(_dropHint); dropBody.Children.Add(_transferSend);
        _dropZone = new Grid { AllowDrop = true, Background = Brush("RDPanelBrush"), CornerRadius = new CornerRadius(16) };
        _dropZone.Children.Add(dropBody); _dropZone.Children.Add(_dropOutline);
        _dropZone.DragOver += (_, e) => {
            if (!_transferSend.IsEnabled || !e.DataView.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            _dropOutline.Stroke = Brush("RDAccentBrush"); _dropZone.Background = Brush("RDSurfaceBrush");
        };
        _dropZone.DragLeave += (_, _) => { _dropOutline.Stroke = Brush("RDLineStrongBrush"); _dropZone.Background = Brush("RDPanelBrush"); };
        _dropZone.Drop += async (_, e) => {
            _dropOutline.Stroke = Brush("RDLineStrongBrush"); _dropZone.Background = Brush("RDPanelBrush");
            if (!_transferSend.IsEnabled || !e.DataView.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is global::Windows.Storage.StorageFile dropped) await PickAndSendFileAsync(dropped);
        };

        var deviceStack = new StackPanel { Spacing = 10 };
        deviceStack.Children.Add(_transferDevices);

        _transferName = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush"), TextWrapping = TextWrapping.Wrap, TextReadingOrder = TextReadingOrder.DetectFromContent };
        _transferState = Txt("", "RDTextHelper");
        AutomationProperties.SetLiveSetting(_transferState, AutomationLiveSetting.Polite);
        _transferProgress = new RDProgress { Value = 0 };
        _queueTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("RDTextBrush") };
        var queueRow = new Grid { ColumnSpacing = 10 };
        queueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var queueText = new StackPanel { Spacing = 8 };
        queueText.Children.Add(_transferName); queueText.Children.Add(_transferProgress); queueText.Children.Add(_transferState);
        queueRow.Children.Add(queueText); Grid.SetColumn(_transferCancel, 1); queueRow.Children.Add(_transferCancel);
        var queue = new StackPanel { Spacing = 12 };
        queue.Children.Add(_queueTitle); queue.Children.Add(queueRow);
        _queueCard = CardBox(queue);

        _receiveFolderHelp = Txt("", "RDTextHelper");
        _receiveFolderStatus = Mono("");
        _receiveFolderChoose = new Button { Style = St("RDSecondaryButton") };
        _receiveFolderChoose.Click += async (_, _) => await PickRememberedReceiveFolderAsync();
        _receiveFolderForget = new Button { Style = St("RDGhostButton") };
        _receiveFolderForget.Click += (_, _) => { App.AppSettings.SetReceiveFolder(""); RenderReceiveFolder(); };
        var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderActions.Children.Add(_receiveFolderChoose); folderActions.Children.Add(_receiveFolderForget);
        var folderHeader = CardHeader("Folder", ""); _folderTitle = (TextBlock)folderHeader.Children[1];
        var folder = new StackPanel { Spacing = 10 };
        folder.Children.Add(folderHeader); folder.Children.Add(_receiveFolderHelp); folder.Children.Add(_receiveFolderStatus); folder.Children.Add(folderActions);

        _transferLimits = Txt("", "RDTextHelper");
        var transferHeader = new StackPanel { Spacing = 6 }; transferHeader.Children.Add(_transferTitle); transferHeader.Children.Add(_transferHelp); TransfersContent.Children.Add(transferHeader);
        TransfersContent.Children.Add(CardBox(deviceStack)); TransfersContent.Children.Add(_dropZone);
        TransfersContent.Children.Add(_queueCard); TransfersContent.Children.Add(CardBox(folder)); TransfersContent.Children.Add(_transferLimits);
        Loaded += (_, _) => {
            if (_transfersAttached) return;
            App.Session.FileReceiveRequested += ChooseReceiveDestinationAsync;
            App.Session.FileProgressChanged += OnFileProgress;
            _transfersAttached = true; RefreshTransferConnections();
        };
        Unloaded += (_, _) => {
            if (!_transfersAttached) return;
            App.Session.FileReceiveRequested -= ChooseReceiveDestinationAsync;
            App.Session.FileProgressChanged -= OnFileProgress;
            _transfersAttached = false; _transferViews.Clear(); _transferFeedback.Message = "";
        };
    }

    private void TransfersItem_Click(object sender, RoutedEventArgs e) { Navigate("transfers"); RefreshTransferConnections(); }
    private void LocalizeTransfers()
    {
        TransfersNavLabel.Text = CompactTransfersItem.Text = _transferTitle.Text = Strings.Get("transfer.title");
        SetAccessibleName(TransfersItem, "transfer.title");
        AutomationProperties.SetName(_transferDevices, Strings.Get("transfer.chooseDevice"));
        AutomationProperties.SetName(_transferProgress, Strings.Get("transfer.progress"));
        _transferDevices.PlaceholderText = Strings.Get("transfer.chooseDevice");
        _dropTitle.Text = Strings.Get("transfer.dropTitle"); _dropHint.Text = Strings.Get("transfer.dropHint");
        _queueTitle.Text = Strings.Get("transfer.queue"); _folderTitle.Text = Strings.Get("transfer.rememberFolder");
        _transferHelp.Text = Strings.Get("transfer.help"); _transferLimits.Text = Strings.Get("transfer.limits");
        _transferSend.Content = Strings.Get("transfer.send"); _transferCancel.Content = Strings.Get("common.cancel");
        _receiveFolderChoose.Content = Strings.Get("transfer.chooseFolder");
        _receiveFolderForget.Content = Strings.Get("transfer.forgetFolder");
        RenderReceiveFolder();
        RefreshTransferConnections();
    }
    private void RefreshTransferConnections()
    {
        if (_transferDevices is null) return;
        foreach (var item in _transferViews.Where(p => !App.Session.IsCurrentSession(p.Key, p.Value.SessionId)).ToArray()) _transferViews.Remove(item.Key);
        var devices = App.Session.TrustedDevices.GetAll().Where(d => HubConnected(d.DeviceId)).ToArray();
        _refreshingTransfers = true;
        var choices = devices.Select(device => new TransferChoice(device.DeviceId, DeviceLabel(device))).ToList();
        _transferDevices.ItemsSource = choices;
        if (!devices.Any(d => d.DeviceId == _transferDevice)) _transferDevice = devices.FirstOrDefault()?.DeviceId;
        _transferDevices.SelectedIndex = choices.FindIndex(c => c.Tag == _transferDevice);
        _refreshingTransfers = false; RenderTransfer();
    }
    private static bool TransferActive(string state) => state is "preparing" or "sending" or "receiving";
    private void RenderTransfer()
    {
        if (_transferState is null) return;
        TransferView? view = null;
        if (_transferDevice is not null && _transferViews.TryGetValue(_transferDevice, out var current) &&
            App.Session.IsCurrentSession(_transferDevice, current.SessionId)) view = current;
        var active = view is not null && TransferActive(view.Status.State);
        _transferSend.IsEnabled = !_choosingTransfer && !active && HubConnected(_transferDevice);
        _transferDevices.IsEnabled = !_choosingTransfer;
        _transferCancel.IsEnabled = active;
        RenderReceiveFolder();
        _transferName.Text = view?.Name ?? Strings.Get("transfer.noFile");
        _transferProgress.IsIndeterminate = view?.Status.State == "preparing";
        _transferProgress.Value = view is { Status.Total: > 0 } ? Math.Clamp(100d * view.Status.Bytes / view.Status.Total, 0, 100) : 0;
        _transferProgress.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
        _transferCancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _transferState.Text = view is null ? Strings.Get(HubConnected(_transferDevice) ? "transfer.ready" : "transfer.offline") :
            Strings.Get("transfer.state." + view.Status.State) + (view.Status.Total > 0 ? $" · {FormatTransferBytes(view.Status.Bytes)} / {FormatTransferBytes(view.Status.Total)}" : "");
    }
    private static string FormatTransferBytes(long bytes) => N(bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : $"{bytes / 1024d:0.0} KiB");
    private void OnFileProgress(string deviceId, string sessionId, TransferStatus status) => DispatcherQueue.PostSafe(() => {
        if (!_transfersAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return;
        var name = _transferViews.TryGetValue(deviceId, out var previous) && previous.SessionId == sessionId ? previous.Name : Strings.Get("transfer.file");
        _transferViews[deviceId] = new(sessionId, name, status);
        RenderTransfer();
    });

    private async Task PickAndSendFileAsync(global::Windows.Storage.StorageFile? dropped = null)
    {
        if (_choosingTransfer || _transferDevice is null) return;
        var deviceId = _transferDevice;
        var sessionId = App.Session.TryGetSessionId(deviceId);
        if (sessionId is null) return;
        _transferFeedback.Message = "";
        _choosingTransfer = true; RenderTransfer();
        try
        {
            var file = dropped;
            if (file is null)
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                file = await picker.PickSingleFileAsync();
            }
            if (file is null || !_transfersAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return;
            var properties = await file.GetBasicPropertiesAsync();
            if (!App.Session.IsCurrentSession(deviceId, sessionId)) return;
            if (properties.Size > (ulong)FileTransferEngine.MaximumBytes || !FileTransferEngine.SafeName(file.Name))
            {
                _transferFeedback.Message = Strings.Get("transfer.invalidFile"); _transferFeedback.IsOpen = true; return;
            }
            var stream = await file.OpenStreamForReadAsync();
            if (!App.Session.IsCurrentSession(deviceId, sessionId)) { stream.Dispose(); return; }
            _transferViews[deviceId] = new(sessionId, file.Name, new("", 0, (long)properties.Size, "preparing"));
            RenderTransfer();
            var success = await App.Session.SendFileAsync(deviceId, stream, file.Name, (long)properties.Size, sessionId);
            if (!_transfersAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return;
            if (_transferViews.TryGetValue(deviceId, out var view))
                _transferViews[deviceId] = view with { Status = view.Status with { State = success ? "completed" : "failed_or_canceled" } };
            RenderTransfer();
        }
        catch (Exception)
        {
            if (_transfersAttached && App.Session.IsCurrentSession(deviceId, sessionId))
            {
                if (_transferViews.TryGetValue(deviceId, out var view) && view.SessionId == sessionId)
                    _transferViews[deviceId] = view with { Status = view.Status with { State = "failed_or_canceled" } };
                _transferFeedback.Message = Strings.Get("transfer.unavailable"); _transferFeedback.IsOpen = true;
            }
        }
        finally { _choosingTransfer = false; RenderTransfer(); }
    }

    private void RenderReceiveFolder()
    {
        var folder = App.AppSettings.ReceiveFolder;
        _receiveFolderStatus.Text = folder.Length == 0 ? Strings.Get("transfer.folderUnset") : folder;
        // Unset is a sentence (body font, muted, reading direction); a real path is mono LTR.
        var isPath = folder.Length > 0;
        _receiveFolderStatus.FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources[isPath ? "RDFontMono" : "RDFontUi"];
        _receiveFolderStatus.FontSize = isPath ? (double)Application.Current.Resources["RDTypeLabel"] : (double)Application.Current.Resources["RDTypeBody"];
        _receiveFolderStatus.Foreground = Brush(isPath ? "RDTextFaintBrush" : "RDTextMutedBrush");
        _receiveFolderStatus.FlowDirection = isPath ? FlowDirection.LeftToRight : FlowDirection;
        _receiveFolderHelp.Text = Strings.Get("transfer.rememberHelp") + (folder.Length > 0 && !Directory.Exists(folder) ? " " + Strings.Get("transfer.folderUnavailable") : "");
        var busy = _choosingReceiveFolder || _transferViews.Values.Any(v => TransferActive(v.Status.State));
        _receiveFolderForget.IsEnabled = folder.Length > 0 && !busy;
        _receiveFolderChoose.IsEnabled = !busy;
    }
    private async Task PickRememberedReceiveFolderAsync()
    {
        if (_choosingReceiveFolder) return;
        _choosingReceiveFolder = true; RenderReceiveFolder();
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads, CommitButtonText = Strings.Get("transfer.rememberFolder") };
        picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        try { var folder = await picker.PickSingleFolderAsync(); if (folder is not null) App.AppSettings.SetReceiveFolder(folder.Path); }
        catch { _transferFeedback.Message = Strings.Get("transfer.folderUnavailable"); _transferFeedback.IsOpen = true; }
        finally { _choosingReceiveFolder = false; RenderReceiveFolder(); }
    }
    private TransferDestination RememberedDestination(string deviceId, string sessionId, FileTransferOffer offer, string folder)
    {
        string? createdPath = null;
        _transferViews[deviceId] = new(sessionId, offer.FileName, new(offer.TransferId, 0, (long)offer.FileSizeBytes, "receiving"));
        _transferDevice = deviceId; RefreshTransferConnections();
        return new TransferDestination(() => {
            if (!App.Session.IsCurrentSession(deviceId, sessionId)) throw new UnauthorizedAccessException();
            try { return ReceiveFolderPolicy.CreateUnique(folder, offer.FileName, out createdPath); }
            catch { DispatcherQueue.PostSafe(() => { _transferFeedback.Message = Strings.Get("transfer.folderUnavailable"); _transferFeedback.IsOpen = true; RenderReceiveFolder(); }); throw; }
        }, () => { if (createdPath is not null && File.Exists(createdPath)) File.Delete(createdPath); });
    }
    private Task<TransferDestination?> ChooseReceiveDestinationAsync(string deviceId, string sessionId, FileTransferOffer offer) =>
        OnHubUiAsync<TransferDestination?>(async cancellation => {
            if (!_transfersAttached || !App.Session.IsCurrentSession(deviceId, sessionId) || !FileTransferEngine.ValidOffer(offer) ||
                !ReceiveFolderPolicy.SafeName(offer.FileName)) return null;
            var remembered = App.AppSettings.ReceiveFolder;
            if (remembered.Length > 0 && Directory.Exists(remembered)) return RememberedDestination(deviceId, sessionId, offer, remembered);
            if (remembered.Length > 0) { _transferFeedback.Message = Strings.Get("transfer.folderUnavailable"); _transferFeedback.IsOpen = true; }
            await _consentDialog.WaitAsync(cancellation);
            try
            {
                (Application.Current as App)?.ShowMainWindow();
                var device = App.Session.TrustedDevices.GetAll().FirstOrDefault(d => d.DeviceId == deviceId);
                if (device is null) return null;
                var body = new StackPanel { Spacing = 12 };
                body.Children.Add(HubText(DeviceLabel(device), 17, true));
                body.Children.Add(HubText(offer.FileName, 16, true));
                body.Children.Add(HubText(FormatTransferBytes((long)offer.FileSizeBytes), 13));
                body.Children.Add(HubText(Strings.Get("transfer.receiveBody"), 13));
                var dialog = Dlg(Strings.Get("transfer.receive"), body, Strings.Get("transfer.chooseFolder"), Strings.Get("common.cancel"));
                using var expiry = cancellation.Register(() => DispatcherQueue.PostSafe(dialog.Hide));
                if (cancellation.IsCancellationRequested || await dialog.ShowAsync() != ContentDialogResult.Primary || cancellation.IsCancellationRequested ||
                    !App.Session.IsCurrentSession(deviceId, sessionId)) return null;
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads, CommitButtonText = Strings.Get("transfer.chooseFolder") };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                var operation = picker.PickSingleFolderAsync();
                using var cancelPicker = cancellation.Register(operation.Cancel);
                var folder = await operation;
                if (folder is null || cancellation.IsCancellationRequested || !_transfersAttached || !App.Session.IsCurrentSession(deviceId, sessionId)) return null;
                return RememberedDestination(deviceId, sessionId, offer, folder.Path);
            }
            finally { _consentDialog.Release(); }
        }, null);
}

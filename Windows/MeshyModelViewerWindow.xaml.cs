using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Malie.Infrastructure;
using Malie.Models;
using Malie.Services;
using Microsoft.Web.WebView2.Core;
using WinOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Malie.Windows;

public partial class MeshyModelViewerWindow : Window
{
    private const string ViewerVirtualHostName = "meshy-viewer.local";
    private const string CacheRoutePrefix = "/__meshy_cache/";
    private const int CustomPromptMaxLength = MeshySceneService.MaxCustomPromptLength;

    private readonly MeshySceneService _meshySceneService;
    private readonly DispatcherTimer _rotationTimer = new();
    private readonly ObservableCollection<ModelTableRow> _rows = new();
    private readonly List<string> _downloadQueue = new();
    private readonly HashSet<string> _queuedPoiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _statusOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueSync = new();

    private MeshyViewerSettings _viewerSettings;
    private IReadOnlyList<string> _currentPointsOfInterest = Array.Empty<string>();
    private IReadOnlyList<ModelListItem> _modelItems = Array.Empty<ModelListItem>();

    private CancellationTokenSource? _queueCts;
    private bool _isViewerReady;
    private bool _webViewEventsAttached;
    private bool _isSyncingSelection;
    private bool _isQueueRunning;
    private bool _isCustomPromptSubmissionRunning;
    private bool _isApplyingCustomPromptTextGuard;
    private string _currentLocationQuery;
    private string _meshyApiKey;
    private string? _activeQueuePoiKey;

    public event EventHandler<MeshyViewerSettings>? ViewerSettingsChanged;
    public event EventHandler<string>? DebugMessage;

    public MeshyModelViewerWindow(
        MeshySceneService meshySceneService,
        MeshyViewerSettings initialViewerSettings,
        string locationQuery,
        IReadOnlyList<string> pointsOfInterest,
        string meshyApiKey)
    {
        _meshySceneService = meshySceneService;
        _viewerSettings = initialViewerSettings.Normalize();
        _currentLocationQuery = locationQuery?.Trim() ?? string.Empty;
        _currentPointsOfInterest = NormalizePoiList(_meshySceneService.ApplyPoiNameOverrides(pointsOfInterest));
        _meshyApiKey = meshyApiKey?.Trim() ?? string.Empty;

        _rotationTimer.Tick += OnRotationTimerTick;

        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        ModelTableDataGrid.ItemsSource = _rows;
        RotationMinutesTextBox.Text = _viewerSettings.RotationMinutes <= 0
            ? "0"
            : _viewerSettings.RotationMinutes.ToString("0.###", CultureInfo.InvariantCulture);
        UpdateCustomPromptCounter();
        UpdateCustomPromptInputState();
        UpdateQueueStatusText();
    }

    public void SetViewerSettings(MeshyViewerSettings viewerSettings)
    {
        _viewerSettings = viewerSettings.Normalize();
        RotationMinutesTextBox.Text = _viewerSettings.RotationMinutes <= 0
            ? "0"
            : _viewerSettings.RotationMinutes.ToString("0.###", CultureInfo.InvariantCulture);

        if (_modelItems.Count > 0)
        {
            var index = ResolvePreferredModelIndex(_modelItems);
            if (index >= 0)
            {
                SelectModelByIndex(index, shouldLoad: _isViewerReady);
            }
        }

        UpdateRotationTimer();
    }

    public void SetMeshyContext(
        string locationQuery,
        IReadOnlyList<string> pointsOfInterest,
        string meshyApiKey)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetMeshyContext(locationQuery, pointsOfInterest, meshyApiKey));
            return;
        }

        _currentLocationQuery = locationQuery?.Trim() ?? string.Empty;
        _currentPointsOfInterest = NormalizePoiList(_meshySceneService.ApplyPoiNameOverrides(pointsOfInterest));
        _meshyApiKey = meshyApiKey?.Trim() ?? string.Empty;
        UpdateCustomPromptInputState();
        RefreshModelList();
        _ = StartQueueProcessorIfNeededAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isViewerReady || _webViewEventsAttached)
        {
            return;
        }

        var viewerRootPath = Path.Combine(AppContext.BaseDirectory, "www");
        var viewerHtmlPath = Path.Combine(viewerRootPath, "mesh-viewer.html");
        if (!File.Exists(viewerHtmlPath))
        {
            SelectedModelPathTextBlock.Text = $"Viewer page missing: {viewerHtmlPath}";
            return;
        }

        var webViewUserDataRoot = AppBranding.GetWebView2UserDataRoot("meshy-viewer");
        Directory.CreateDirectory(webViewUserDataRoot);
        var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewUserDataRoot);

        await ViewerWebView.EnsureCoreWebView2Async(webViewEnvironment);
        ViewerWebView.NavigationCompleted += OnViewerNavigationCompleted;
        ViewerWebView.CoreWebView2.WebMessageReceived += OnViewerWebMessageReceived;
        ViewerWebView.CoreWebView2.WebResourceRequested += OnViewerWebResourceRequested;
        ViewerWebView.CoreWebView2.AddWebResourceRequestedFilter(
            $"https://{ViewerVirtualHostName}{CacheRoutePrefix}*",
            CoreWebView2WebResourceContext.All);
        _webViewEventsAttached = true;

        ViewerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ViewerVirtualHostName,
            viewerRootPath,
            CoreWebView2HostResourceAccessKind.Allow);
        ViewerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            MeshySceneService.CacheVirtualHostName,
            _meshySceneService.CacheRootPath,
            CoreWebView2HostResourceAccessKind.Allow);

        SelectedModelPathTextBlock.Text = "Loading cached model viewer...";
        ViewerWebView.Source = new Uri($"https://{ViewerVirtualHostName}/mesh-viewer.html");
    }

    private void OnRefreshListClick(object sender, RoutedEventArgs e)
    {
        RefreshModelList(shouldLoadSelectedModel: false);
    }

    private void OnCancelQueueClick(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? queueCts;
        lock (_queueSync)
        {
            _downloadQueue.Clear();
            _queuedPoiKeys.Clear();
            queueCts = _queueCts;
            _queueCts = null;
            _isQueueRunning = false;
            _activeQueuePoiKey = null;
        }

        queueCts?.Cancel();
        queueCts?.Dispose();

        RefreshModelList(shouldLoadSelectedModel: false);
        SelectedModelPathTextBlock.Text = "Meshy queue canceled.";
        EmitDebug("Queue canceled by user.");
    }

    private void OnImportGlbClick(object sender, RoutedEventArgs e)
    {
        var dialog = new WinOpenFileDialog
        {
            CheckFileExists = true,
            Filter = "GLB files (*.glb)|*.glb|All files (*.*)|*.*",
            Title = "Import GLB Model"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var defaultPoiName = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (!_meshySceneService.TryImportGlbModel(dialog.FileName, defaultPoiName, out var imported, out var message) ||
            imported is null)
        {
            SelectedModelPathTextBlock.Text = message;
            return;
        }

        _currentPointsOfInterest = _currentPointsOfInterest
            .Concat(new[] { imported.Name })
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Select(point => point.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _viewerSettings = _viewerSettings with { ActiveModelRelativePath = imported.LocalRelativePath };
        RaiseViewerSettingsChanged();

        RefreshModelList(
            preferredRowIdentity: NormalizeRelativePath(imported.LocalRelativePath),
            preferredPoiKey: NormalizeTargetKey(imported.Name),
            shouldLoadSelectedModel: true);
        SelectedModelPathTextBlock.Text = message;
    }

    private void OnQueueModelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelTableRow row)
        {
            return;
        }

        EmitDebug($"Queued POI '{row.PoiName}' for Meshy generation.");
        QueuePoi(row, prioritize: false);
    }

    private void OnDownloadNowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelTableRow row)
        {
            return;
        }

        EmitDebug($"Priority download requested for POI '{row.PoiName}'.");
        QueuePoi(row, prioritize: true);
    }

    private void OnDeleteModelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelTableRow row || !row.CanDelete)
        {
            return;
        }

        var confirmation = WpfMessageBox.Show(
            this,
            $"Delete cached model '{row.ModelFileName}' for '{row.PoiName}'?",
            "Delete Cached Model",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_meshySceneService.TryDeleteCachedModel(row.LocalRelativePath, out var message))
        {
            SelectedModelPathTextBlock.Text = message;
            return;
        }

        if (string.Equals(_viewerSettings.ActiveModelRelativePath, row.LocalRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _viewerSettings = _viewerSettings with { ActiveModelRelativePath = string.Empty };
            RaiseViewerSettingsChanged();
        }

        RefreshModelList(preferredPoiKey: row.PoiKey, shouldLoadSelectedModel: false);
        SelectedModelPathTextBlock.Text = message;
    }

    private void OnModelTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || !_isViewerReady)
        {
            return;
        }

        if (ModelTableDataGrid.SelectedItem is not ModelTableRow selectedRow)
        {
            return;
        }

        if (!selectedRow.IsCachedModel || string.IsNullOrWhiteSpace(selectedRow.LocalRelativePath))
        {
            SelectedModelPathTextBlock.Text = $"POI '{selectedRow.PoiName}' has no cached model yet.";
            return;
        }

        var index = FindModelIndexByPath(selectedRow.LocalRelativePath);
        if (index < 0)
        {
            SelectedModelPathTextBlock.Text = $"Cached model not found in viewer list: {selectedRow.LocalRelativePath}";
            return;
        }

        SelectModelByIndex(index, shouldLoad: true);
    }

    private void OnModelTableCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not ModelTableRow row ||
            e.EditingElement is not WpfTextBox editor)
        {
            return;
        }

        var oldName = row.PoiName?.Trim() ?? string.Empty;
        var newName = editor.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            e.Cancel = true;
            SelectedModelPathTextBlock.Text = "POI name cannot be empty.";
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (row.IsCachedModel)
        {
            if (!_meshySceneService.TryRenameCachedTargetByModelPath(row.LocalRelativePath, newName, out var renameMessage))
            {
                e.Cancel = true;
                SelectedModelPathTextBlock.Text = renameMessage;
                return;
            }

            _meshySceneService.SetPoiNameOverride(oldName, newName);
            ReplacePoiNameInContext(oldName, newName);
            MoveStatusOverride(oldName, newName);
            ReplacePoiNameInQueue(oldName, newName);
            row.PoiName = newName;
            Dispatcher.BeginInvoke(
                () => RefreshModelList(
                    preferredRowIdentity: row.RowIdentity,
                    preferredPoiKey: NormalizeTargetKey(newName),
                    shouldLoadSelectedModel: false),
                DispatcherPriority.Background);
            SelectedModelPathTextBlock.Text = renameMessage;
            return;
        }

        _meshySceneService.SetPoiNameOverride(oldName, newName);
        ReplacePoiNameInContext(oldName, newName);
        MoveStatusOverride(oldName, newName);
        ReplacePoiNameInQueue(oldName, newName);
        row.PoiName = newName;
        RefreshModelList(
            preferredRowIdentity: $"missing::{NormalizeTargetKey(newName)}",
            preferredPoiKey: NormalizeTargetKey(newName),
            shouldLoadSelectedModel: false);
        SelectedModelPathTextBlock.Text = $"Updated POI name to '{newName}'.";
    }

    private void OnApplyViewerSettingsClick(object sender, RoutedEventArgs e)
    {
        var minutes = ParseRotationMinutesFromUi();
        RotationMinutesTextBox.Text = minutes <= 0
            ? "0"
            : minutes.ToString("0.###", CultureInfo.InvariantCulture);

        var activePath = _viewerSettings.ActiveModelRelativePath;
        if (ModelTableDataGrid.SelectedItem is ModelTableRow selectedRow &&
            selectedRow.IsCachedModel &&
            !string.IsNullOrWhiteSpace(selectedRow.LocalRelativePath))
        {
            activePath = selectedRow.LocalRelativePath;
            var selectedIndex = FindModelIndexByPath(activePath);
            if (selectedIndex >= 0)
            {
                SelectModelByIndex(selectedIndex, shouldLoad: _isViewerReady);
            }
        }

        _viewerSettings = new MeshyViewerSettings(activePath, minutes).Normalize();
        RaiseViewerSettingsChanged();
        UpdateRotationTimer();

        SelectedModelPathTextBlock.Text =
            minutes > 0
                ? $"Viewer settings applied. Auto-rotate every {minutes:0.###} minute(s)."
                : "Viewer settings applied. Auto-rotate disabled.";
    }

    private void OnCustomPromptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplyingCustomPromptTextGuard)
        {
            return;
        }

        var promptText = CustomPromptTextBox.Text ?? string.Empty;
        if (promptText.Length > CustomPromptMaxLength)
        {
            _isApplyingCustomPromptTextGuard = true;
            CustomPromptTextBox.Text = promptText[..CustomPromptMaxLength];
            CustomPromptTextBox.CaretIndex = CustomPromptTextBox.Text.Length;
            _isApplyingCustomPromptTextGuard = false;
            ShowCustomPromptLimitMessage();
        }
        else if (promptText.Length == CustomPromptMaxLength)
        {
            ShowCustomPromptLimitMessage();
        }
        else
        {
            CustomPromptLimitTextBlock.Visibility = Visibility.Collapsed;
        }

        UpdateCustomPromptCounter();
        UpdateCustomPromptInputState();
    }

    private void OnCustomPromptPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (GetProspectivePromptLength(e.Text) <= CustomPromptMaxLength)
        {
            return;
        }

        e.Handled = true;
        ShowCustomPromptLimitMessage();
    }

    private void OnCustomPromptPaste(object sender, DataObjectPastingEventArgs e)
    {
        var pastedText =
            e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText, true) as string ??
            e.SourceDataObject.GetData(System.Windows.DataFormats.Text, true) as string ??
            string.Empty;
        if (string.IsNullOrEmpty(pastedText))
        {
            return;
        }

        var available = CustomPromptMaxLength - GetPromptLengthWithoutSelection();
        if (available <= 0)
        {
            e.CancelCommand();
            ShowCustomPromptLimitMessage();
            return;
        }

        if (pastedText.Length <= available)
        {
            return;
        }

        e.CancelCommand();
        InsertPromptAtSelection(pastedText[..available]);
        ShowCustomPromptLimitMessage();
    }

    private async void OnSubmitCustomPromptClick(object sender, RoutedEventArgs e)
    {
        if (_isCustomPromptSubmissionRunning)
        {
            return;
        }

        if (!CanQueueMeshyDownloads())
        {
            SelectedModelPathTextBlock.Text = "Cannot submit custom prompt: Meshy API key is missing.";
            return;
        }

        var prompt = (CustomPromptTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            SelectedModelPathTextBlock.Text = "Custom prompt is empty.";
            return;
        }

        if (prompt.Length > CustomPromptMaxLength)
        {
            prompt = prompt[..CustomPromptMaxLength];
        }

        _isCustomPromptSubmissionRunning = true;
        UpdateCustomPromptInputState();

        try
        {
            SelectedModelPathTextBlock.Text = "Submitting custom prompt to Meshy...";
            EmitDebug($"Meshy custom prompt submitted ({prompt.Length} chars).");

            var result = await _meshySceneService.GenerateFromCustomPromptAsync(
                prompt,
                _meshyApiKey,
                CancellationToken.None,
                progressCallback: message =>
                {
                    if (!Dispatcher.CheckAccess())
                    {
                        Dispatcher.Invoke(() =>
                        {
                            SelectedModelPathTextBlock.Text = message;
                            EmitDebug(message);
                        });
                        return;
                    }

                    SelectedModelPathTextBlock.Text = message;
                    EmitDebug(message);
                });

            if (result.UsedMeshy && result.GeneratedMeshes.Count > 0)
            {
                var firstMesh = result.GeneratedMeshes[0];
                RefreshModelList(
                    preferredRowIdentity: NormalizeRelativePath(firstMesh.LocalRelativePath),
                    preferredPoiKey: NormalizeTargetKey(firstMesh.Name),
                    shouldLoadSelectedModel: true);
                SelectedModelPathTextBlock.Text = result.Message;
                EmitDebug($"Meshy custom prompt generated {result.GeneratedMeshes.Count} mesh file(s).");
                return;
            }

            SelectedModelPathTextBlock.Text = $"Failed: {result.Message}";
            EmitDebug($"Meshy custom prompt returned no output: {result.Message}");
        }
        catch (OperationCanceledException)
        {
            SelectedModelPathTextBlock.Text = "Meshy custom prompt canceled.";
            EmitDebug("Meshy custom prompt canceled.");
        }
        catch (Exception ex)
        {
            SelectedModelPathTextBlock.Text = $"Meshy custom prompt failed: {ex.Message}";
            EmitDebug($"Meshy custom prompt failed: {ex.Message}");
        }
        finally
        {
            _isCustomPromptSubmissionRunning = false;
            UpdateCustomPromptInputState();
        }
    }

    private void QueuePoi(ModelTableRow row, bool prioritize)
    {
        if (row.IsCachedModel)
        {
            row.StatusText = "Cached";
            row.RefreshActionState(CanQueueMeshyDownloads());
            EmitDebug($"Skipped queue for '{row.PoiName}' because model is already cached.");
            return;
        }

        var canQueue = CanQueueMeshyDownloads();
        if (!canQueue)
        {
            row.StatusText = "Cannot queue (Meshy API key missing).";
            _statusOverrides[row.PoiKey] = row.StatusText;
            row.RefreshActionState(canQueue);
            UpdateQueueStatusText();
            EmitDebug($"Queue blocked for '{row.PoiName}': {row.StatusText}");
            return;
        }

        var queued = false;
        lock (_queueSync)
        {
            if (_queuedPoiKeys.Contains(row.PoiKey) ||
                string.Equals(_activeQueuePoiKey, row.PoiKey, StringComparison.OrdinalIgnoreCase))
            {
                queued = true;
            }
            else
            {
                if (prioritize)
                {
                    _downloadQueue.Insert(0, row.PoiName);
                }
                else
                {
                    _downloadQueue.Add(row.PoiName);
                }

                _queuedPoiKeys.Add(row.PoiKey);
            }
        }

        if (queued)
        {
            row.IsQueued = true;
            row.StatusText = "Already queued.";
            row.RefreshActionState(canQueue);
            UpdateQueueStatusText();
            EmitDebug($"POI '{row.PoiName}' is already queued.");
            return;
        }

        _statusOverrides.Remove(row.PoiKey);
        row.IsQueued = true;
        row.StatusText = prioritize ? "Queued (priority)." : "Queued.";
        row.RefreshActionState(canQueue);
        UpdateQueueStatusText();
        EmitDebug($"POI '{row.PoiName}' added to Meshy queue ({(prioritize ? "priority" : "normal")}).");
        _ = StartQueueProcessorIfNeededAsync();
    }

    private async Task StartQueueProcessorIfNeededAsync()
    {
        if (!CanQueueMeshyDownloads())
        {
            UpdateQueueStatusText();
            return;
        }

        lock (_queueSync)
        {
            if (_isQueueRunning || _downloadQueue.Count == 0)
            {
                UpdateQueueStatusText();
                return;
            }

            _isQueueRunning = true;
            _queueCts ??= new CancellationTokenSource();
        }

        try
        {
            while (true)
            {
                string? poiName = null;
                string poiKey = string.Empty;
                CancellationToken cancellationToken;

                lock (_queueSync)
                {
                    _queueCts ??= new CancellationTokenSource();
                    cancellationToken = _queueCts.Token;

                    if (_downloadQueue.Count == 0)
                    {
                        break;
                    }

                    poiName = _downloadQueue[0];
                    _downloadQueue.RemoveAt(0);
                    poiKey = NormalizeTargetKey(poiName);
                    _queuedPoiKeys.Remove(poiKey);
                    _activeQueuePoiKey = poiKey;
                }

                var row = FindRowByPoiKey(poiKey);
                if (row is not null)
                {
                    row.IsQueued = false;
                    row.IsDownloading = true;
                    row.StatusText = "Downloading from Meshy...";
                    row.RefreshActionState(CanQueueMeshyDownloads());
                }
                UpdateQueueStatusText();
                EmitDebug($"Queue processing started for '{poiName}'.");

                try
                {
                    var result = await _meshySceneService.GenerateTexturedLandmarkReferencesAsync(
                        cityName: string.Empty,
                        weather: CreateQueueWeatherStub(),
                        pointsOfInterest: new[] { poiName! },
                        apiKey: _meshyApiKey,
                        forceRetrySkippedTargets: true,
                        cancellationToken: cancellationToken,
                        progressCallback: message =>
                        {
                            if (!Dispatcher.CheckAccess())
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    SelectedModelPathTextBlock.Text = message;
                                    EmitDebug(message);
                                });
                                return;
                            }

                            SelectedModelPathTextBlock.Text = message;
                            EmitDebug(message);
                        });

                    if (result.UsedMeshy && result.GeneratedMeshes.Count > 0)
                    {
                        _statusOverrides.Remove(poiKey);
                        SelectedModelPathTextBlock.Text =
                            $"Meshy model ready for '{poiName}': {result.GeneratedMeshes.Count} file(s).";
                        EmitDebug($"Meshy model ready for '{poiName}': {result.GeneratedMeshes.Count} file(s).");
                    }
                    else
                    {
                        var failureMessage = $"Failed: {Truncate(result.Message, 180)}";
                        _statusOverrides[poiKey] = failureMessage;
                        if (row is not null)
                        {
                            row.StatusText = failureMessage;
                        }

                        EmitDebug($"Meshy generation returned no output for '{poiName}': {result.Message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    var canceledMessage = "Canceled (marked to skip by Meshy service).";
                    _statusOverrides[poiKey] = canceledMessage;
                    if (row is not null)
                    {
                        row.StatusText = canceledMessage;
                    }

                    SelectedModelPathTextBlock.Text = $"Meshy generation canceled for '{poiName}'.";
                    EmitDebug($"Meshy generation canceled for '{poiName}'.");
                }
                catch (Exception ex)
                {
                    var failureMessage = $"Failed: {Truncate(ex.Message, 180)}";
                    _statusOverrides[poiKey] = failureMessage;
                    if (row is not null)
                    {
                        row.StatusText = failureMessage;
                    }

                    SelectedModelPathTextBlock.Text = $"Meshy generation failed for '{poiName}': {ex.Message}";
                    EmitDebug($"Meshy generation failed for '{poiName}': {ex.Message}");
                }
                finally
                {
                    if (row is not null)
                    {
                        row.IsDownloading = false;
                        row.IsQueued = false;
                    }

                    lock (_queueSync)
                    {
                        _activeQueuePoiKey = null;
                    }

                    RefreshModelList(preferredPoiKey: poiKey, shouldLoadSelectedModel: false);
                    EmitDebug($"Queue processing finished for '{poiName}'.");
                }
            }
        }
        finally
        {
            CancellationTokenSource? toDispose;
            lock (_queueSync)
            {
                _isQueueRunning = false;
                toDispose = _queueCts;
                _queueCts = null;
                _activeQueuePoiKey = null;
            }

            toDispose?.Dispose();
            UpdateQueueStatusText();
            EmitDebug("Queue processor is idle.");
        }
    }

    private void RefreshModelList(
        string? preferredRowIdentity = null,
        string? preferredPoiKey = null,
        bool shouldLoadSelectedModel = false)
    {
        var selectedRowIdentity = preferredRowIdentity;
        var selectedPoiKey = preferredPoiKey;
        if (string.IsNullOrWhiteSpace(selectedRowIdentity) && ModelTableDataGrid.SelectedItem is ModelTableRow selected)
        {
            selectedRowIdentity = selected.RowIdentity;
            selectedPoiKey ??= selected.PoiKey;
        }

        var cachedMeshes = _meshySceneService.GetCachedMeshes();
        _modelItems = BuildModelItemList(cachedMeshes);

        BuildRows(cachedMeshes);
        RestoreRowSelection(selectedRowIdentity, selectedPoiKey);

        if (_modelItems.Count > 0)
        {
            var preferredModelIndex = ResolvePreferredModelIndex(_modelItems);
            SelectModelByIndex(preferredModelIndex, shouldLoadSelectedModel && _isViewerReady);
        }

        UpdateRotationTimer();
        UpdateQueueStatusText();
    }

    private void BuildRows(IReadOnlyList<PointOfInterestMesh> cachedMeshes)
    {
        _rows.Clear();
        var canQueue = CanQueueMeshyDownloads();

        var cachedByPoiKey = cachedMeshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
            .GroupBy(mesh => NormalizeTargetKey(mesh.Name))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var mesh in cachedMeshes
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => Path.GetFileName(item.LocalRelativePath), StringComparer.OrdinalIgnoreCase))
        {
            var poiName = string.IsNullOrWhiteSpace(mesh.Name) ? "Unknown" : mesh.Name;
            var poiKey = NormalizeTargetKey(poiName);
            var modelFile = Path.GetFileName(mesh.LocalRelativePath);
            var row = new ModelTableRow(
                poiName,
                poiKey,
                modelFile,
                "Cached",
                mesh.LocalRelativePath,
                BuildViewerModelUrl(mesh.LocalRelativePath),
                mesh.LocalWebUrl,
                isCachedModel: true,
                canDelete: true);
            row.RefreshActionState(canQueue);
            _rows.Add(row);
        }

        var currentPoiByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var poiName in _currentPointsOfInterest)
        {
            if (string.IsNullOrWhiteSpace(poiName))
            {
                continue;
            }

            var trimmed = poiName.Trim();
            var poiKey = NormalizeTargetKey(trimmed);
            if (string.IsNullOrWhiteSpace(poiKey))
            {
                continue;
            }

            if (!currentPoiByKey.TryGetValue(poiKey, out var existingDisplayName) ||
                IsPreferredPoiDisplayName(trimmed, existingDisplayName))
            {
                currentPoiByKey[poiKey] = trimmed;
            }
        }

        foreach (var poiEntry in currentPoiByKey.OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
        {
            var poiKey = poiEntry.Key;
            var poiName = poiEntry.Value;
            if (cachedByPoiKey.ContainsKey(poiKey))
            {
                continue;
            }

            var status = ResolveMissingPoiStatus(poiKey);
            var row = new ModelTableRow(
                poiName,
                poiKey,
                "-",
                status,
                localRelativePath: string.Empty,
                viewerModelUrl: string.Empty,
                localWebUrl: string.Empty,
                isCachedModel: false,
                canDelete: false);
            row.IsQueued = _queuedPoiKeys.Contains(poiKey);
            row.IsDownloading = string.Equals(_activeQueuePoiKey, poiKey, StringComparison.OrdinalIgnoreCase);
            row.RefreshActionState(canQueue);
            _rows.Add(row);
        }

        if (_rows.Count == 0)
        {
            SelectedModelPathTextBlock.Text = "No cached models or POI rows available.";
        }
    }

    private string ResolveMissingPoiStatus(string poiKey)
    {
        if (string.Equals(_activeQueuePoiKey, poiKey, StringComparison.OrdinalIgnoreCase))
        {
            return "Downloading from Meshy...";
        }

        if (_queuedPoiKeys.Contains(poiKey))
        {
            return "Queued.";
        }

        if (_statusOverrides.TryGetValue(poiKey, out var overrideStatus) && !string.IsNullOrWhiteSpace(overrideStatus))
        {
            return overrideStatus;
        }

        if (string.IsNullOrWhiteSpace(_meshyApiKey))
        {
            return "Missing (Meshy key required)";
        }

        return "Missing";
    }

    private void RestoreRowSelection(string? preferredRowIdentity, string? preferredPoiKey)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        ModelTableRow? selectedRow = null;
        if (!string.IsNullOrWhiteSpace(preferredRowIdentity))
        {
            selectedRow = _rows.FirstOrDefault(row =>
                string.Equals(row.RowIdentity, preferredRowIdentity, StringComparison.OrdinalIgnoreCase));
        }

        if (selectedRow is null && !string.IsNullOrWhiteSpace(preferredPoiKey))
        {
            selectedRow = _rows.FirstOrDefault(row =>
                string.Equals(row.PoiKey, preferredPoiKey, StringComparison.OrdinalIgnoreCase));
        }

        selectedRow ??= _rows[0];

        _isSyncingSelection = true;
        ModelTableDataGrid.SelectedItem = selectedRow;
        _isSyncingSelection = false;
    }

    private IReadOnlyList<ModelListItem> BuildModelItemList(IReadOnlyList<PointOfInterestMesh> cachedModels)
    {
        var items = cachedModels
            .Select(model => new ModelListItem(
                DisplayLabel: $"{model.Name} | {Path.GetFileName(model.LocalRelativePath)}",
                LocalRelativePath: model.LocalRelativePath,
                ViewerModelUrl: BuildViewerModelUrl(model.LocalRelativePath),
                LocalWebUrl: model.LocalWebUrl))
            .DistinctBy(item => item.LocalRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return items
            .OrderBy(item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ReplacePoiNameInContext(string oldName, string newName)
    {
        var oldKey = NormalizeTargetKey(oldName);
        var updated = new List<string>();
        var replaced = false;
        foreach (var point in _currentPointsOfInterest)
        {
            if (string.Equals(NormalizeTargetKey(point), oldKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!replaced)
                {
                    updated.Add(newName);
                    replaced = true;
                }

                continue;
            }

            updated.Add(point);
        }

        if (!replaced)
        {
            updated.Add(newName);
        }

        _currentPointsOfInterest = updated
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Select(point => point.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void MoveStatusOverride(string oldName, string newName)
    {
        var oldKey = NormalizeTargetKey(oldName);
        var newKey = NormalizeTargetKey(newName);
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey) ||
            string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_statusOverrides.TryGetValue(oldKey, out var value))
        {
            _statusOverrides.Remove(oldKey);
            _statusOverrides[newKey] = value;
        }
    }

    private void ReplacePoiNameInQueue(string oldName, string newName)
    {
        var oldKey = NormalizeTargetKey(oldName);
        var newKey = NormalizeTargetKey(newName);
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey))
        {
            return;
        }

        lock (_queueSync)
        {
            for (var index = 0; index < _downloadQueue.Count; index += 1)
            {
                if (string.Equals(NormalizeTargetKey(_downloadQueue[index]), oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    _downloadQueue[index] = newName;
                }
            }

            if (_queuedPoiKeys.Remove(oldKey))
            {
                _queuedPoiKeys.Add(newKey);
            }

            if (string.Equals(_activeQueuePoiKey, oldKey, StringComparison.OrdinalIgnoreCase))
            {
                _activeQueuePoiKey = newKey;
            }
        }
    }

    private bool CanQueueMeshyDownloads()
    {
        return !string.IsNullOrWhiteSpace(_meshyApiKey);
    }

    private void UpdateQueueStatusText()
    {
        var queuedCount = 0;
        var isRunning = false;
        var activePoi = string.Empty;
        lock (_queueSync)
        {
            queuedCount = _downloadQueue.Count;
            isRunning = _isQueueRunning;
            activePoi = _activeQueuePoiKey ?? string.Empty;
        }

        var meshyState = string.IsNullOrWhiteSpace(_meshyApiKey) ? "Meshy key missing." : "Meshy ready.";

        if (isRunning)
        {
            QueueStatusTextBlock.Text = string.IsNullOrWhiteSpace(activePoi)
                ? $"Queue running ({queuedCount} remaining). {meshyState}"
                : $"Queue running '{activePoi}' ({queuedCount} remaining). {meshyState}";
            return;
        }

        QueueStatusTextBlock.Text = queuedCount > 0
            ? $"Queue pending ({queuedCount}). {meshyState}"
            : $"Queue idle. {meshyState}";
    }

    private void UpdateCustomPromptCounter()
    {
        var length = (CustomPromptTextBox.Text ?? string.Empty).Length;
        CustomPromptCountTextBlock.Text = $"{length} / {CustomPromptMaxLength}";
    }

    private void UpdateCustomPromptInputState()
    {
        var canSubmit = CanQueueMeshyDownloads() &&
                        !_isCustomPromptSubmissionRunning &&
                        !string.IsNullOrWhiteSpace((CustomPromptTextBox.Text ?? string.Empty).Trim());
        SubmitCustomPromptButton.IsEnabled = canSubmit;
    }

    private void ShowCustomPromptLimitMessage()
    {
        CustomPromptLimitTextBlock.Text =
            $"Maximum {CustomPromptMaxLength} characters reached. Extra text was not added.";
        CustomPromptLimitTextBlock.Visibility = Visibility.Visible;
    }

    private int GetPromptLengthWithoutSelection()
    {
        var prompt = CustomPromptTextBox.Text ?? string.Empty;
        var selectionLength = Math.Max(0, CustomPromptTextBox.SelectionLength);
        return Math.Max(0, prompt.Length - selectionLength);
    }

    private int GetProspectivePromptLength(string incomingText)
    {
        return GetPromptLengthWithoutSelection() + (incomingText?.Length ?? 0);
    }

    private void InsertPromptAtSelection(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var prompt = CustomPromptTextBox.Text ?? string.Empty;
        var selectionStart = Math.Max(0, CustomPromptTextBox.SelectionStart);
        var selectionLength = Math.Max(0, CustomPromptTextBox.SelectionLength);
        var end = Math.Min(prompt.Length, selectionStart + selectionLength);
        var updated = prompt[..selectionStart] + text + prompt[end..];

        _isApplyingCustomPromptTextGuard = true;
        CustomPromptTextBox.Text = updated;
        CustomPromptTextBox.SelectionStart = Math.Min(updated.Length, selectionStart + text.Length);
        CustomPromptTextBox.SelectionLength = 0;
        _isApplyingCustomPromptTextGuard = false;
        UpdateCustomPromptCounter();
        UpdateCustomPromptInputState();
    }

    private ModelTableRow? FindRowByPoiKey(string poiKey)
    {
        if (string.IsNullOrWhiteSpace(poiKey))
        {
            return null;
        }

        return _rows.FirstOrDefault(row =>
            string.Equals(row.PoiKey, poiKey, StringComparison.OrdinalIgnoreCase));
    }

    private void OnViewerNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            SelectedModelPathTextBlock.Text = $"Viewer failed to load ({e.WebErrorStatus}).";
            return;
        }

        _isViewerReady = true;
        SelectedModelPathTextBlock.Text = "Viewer ready. Select a cached model.";
        RefreshModelList(shouldLoadSelectedModel: true);
        UpdateRotationTimer();
    }

    private void OnViewerWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var payload = JsonDocument.Parse(e.WebMessageAsJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var message = root.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var eventType = root.TryGetProperty("type", out var typeElement) &&
                            typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : string.Empty;

            if (string.Equals(eventType, "load-failed", StringComparison.OrdinalIgnoreCase))
            {
                SelectedModelPathTextBlock.Text = $"Load failed: {message}";
                return;
            }

            if (string.Equals(eventType, "loading", StringComparison.OrdinalIgnoreCase))
            {
                SelectedModelPathTextBlock.Text = message;
                return;
            }

            if (string.Equals(eventType, "model-loaded", StringComparison.OrdinalIgnoreCase))
            {
                SelectedModelPathTextBlock.Text = message;
            }
        }
        catch
        {
            // Ignore malformed messages from webview.
        }
    }

    private void OnViewerWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (ViewerWebView.CoreWebView2 is null)
        {
            return;
        }

        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requestUri))
        {
            return;
        }

        if (!string.Equals(requestUri.Host, ViewerVirtualHostName, StringComparison.OrdinalIgnoreCase) ||
            !requestUri.AbsolutePath.StartsWith(CacheRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relativePath = Uri.UnescapeDataString(requestUri.AbsolutePath[CacheRoutePrefix.Length..])
            .Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var rootedCachePath = Path.GetFullPath(_meshySceneService.CacheRootPath);
        var requestedPath = Path.GetFullPath(Path.Combine(
            _meshySceneService.CacheRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!requestedPath.StartsWith(rootedCachePath, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = ViewerWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null,
                403,
                "Forbidden",
                "Content-Type: text/plain");
            return;
        }

        if (!File.Exists(requestedPath))
        {
            e.Response = ViewerWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null,
                404,
                "Not Found",
                "Content-Type: text/plain");
            return;
        }

        var contentType = GetContentTypeFromExtension(Path.GetExtension(requestedPath));
        var stream = File.Open(requestedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var headers = $"Content-Type: {contentType}\\r\\nCache-Control: no-store\\r\\nAccess-Control-Allow-Origin: *";
        e.Response = ViewerWebView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private async Task LoadSelectedModelAsync(ModelListItem selected)
    {
        if (!_isViewerReady)
        {
            return;
        }

        SelectedModelPathTextBlock.Text = selected.LocalRelativePath;
        _viewerSettings = _viewerSettings with { ActiveModelRelativePath = selected.LocalRelativePath };
        RaiseViewerSettingsChanged();

        var primaryUrlJson = JsonSerializer.Serialize(selected.ViewerModelUrl);
        var fallbackUrlJson = JsonSerializer.Serialize(selected.LocalWebUrl);
        var labelJson = JsonSerializer.Serialize(selected.DisplayLabel);

        try
        {
            await ViewerWebView.ExecuteScriptAsync(
                $"window.loadMeshModelWithFallback({primaryUrlJson}, {fallbackUrlJson}, {labelJson});");
        }
        catch (Exception ex)
        {
            SelectedModelPathTextBlock.Text = $"Failed to load model: {ex.Message}";
        }
    }

    private int ResolvePreferredModelIndex(IReadOnlyList<ModelListItem> items)
    {
        if (items.Count == 0)
        {
            return -1;
        }

        var preferredPath = NormalizeRelativePath(_viewerSettings.ActiveModelRelativePath);
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            for (var index = 0; index < items.Count; index += 1)
            {
                if (string.Equals(NormalizeRelativePath(items[index].LocalRelativePath), preferredPath, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private int FindModelIndexByPath(string localRelativePath)
    {
        var normalized = NormalizeRelativePath(localRelativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return -1;
        }

        for (var index = 0; index < _modelItems.Count; index += 1)
        {
            if (string.Equals(NormalizeRelativePath(_modelItems[index].LocalRelativePath), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindIndex(IReadOnlyList<ModelListItem> items, Func<ModelListItem, bool> predicate)
    {
        for (var index = 0; index < items.Count; index += 1)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private void SelectModelByIndex(int index, bool shouldLoad)
    {
        if (index < 0 || index >= _modelItems.Count)
        {
            return;
        }

        var selected = _modelItems[index];
        _isSyncingSelection = true;
        var matchingRow = _rows.FirstOrDefault(row =>
            row.IsCachedModel &&
            string.Equals(NormalizeRelativePath(row.LocalRelativePath), NormalizeRelativePath(selected.LocalRelativePath), StringComparison.OrdinalIgnoreCase));
        if (matchingRow is not null)
        {
            ModelTableDataGrid.SelectedItem = matchingRow;
        }

        _isSyncingSelection = false;

        if (!shouldLoad)
        {
            return;
        }

        _ = LoadSelectedModelAsync(selected);
    }

    private double ParseRotationMinutesFromUi()
    {
        if (double.TryParse(
                RotationMinutesTextBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                return _viewerSettings.RotationMinutes;
            }

            return Math.Max(0, Math.Min(1440, parsed));
        }

        return _viewerSettings.RotationMinutes;
    }

    private void UpdateRotationTimer()
    {
        _rotationTimer.Stop();
        if (!_isViewerReady)
        {
            return;
        }

        var minutes = ParseRotationMinutesFromUi();
        _viewerSettings = _viewerSettings with { RotationMinutes = minutes };
        if (minutes <= 0)
        {
            return;
        }

        if (_modelItems.Count <= 1)
        {
            return;
        }

        _rotationTimer.Interval = TimeSpan.FromMinutes(minutes);
        _rotationTimer.Start();
    }

    private void OnRotationTimerTick(object? sender, EventArgs e)
    {
        if (!_isViewerReady || _modelItems.Count <= 1)
        {
            return;
        }

        var currentIndex = FindModelIndexByPath(_viewerSettings.ActiveModelRelativePath);
        if (currentIndex < 0 && ModelTableDataGrid.SelectedItem is ModelTableRow selectedRow && selectedRow.IsCachedModel)
        {
            currentIndex = FindModelIndexByPath(selectedRow.LocalRelativePath);
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + 1) % _modelItems.Count;
        SelectModelByIndex(nextIndex, shouldLoad: true);
    }

    private void RaiseViewerSettingsChanged()
    {
        ViewerSettingsChanged?.Invoke(this, _viewerSettings.Normalize());
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _rotationTimer.Stop();
        _rotationTimer.Tick -= OnRotationTimerTick;

        CancellationTokenSource? queueCts;
        lock (_queueSync)
        {
            _downloadQueue.Clear();
            _queuedPoiKeys.Clear();
            queueCts = _queueCts;
            _queueCts = null;
            _isQueueRunning = false;
            _activeQueuePoiKey = null;
        }

        queueCts?.Cancel();
        queueCts?.Dispose();

        if (ViewerWebView.CoreWebView2 is null || !_webViewEventsAttached)
        {
            return;
        }

        ViewerWebView.NavigationCompleted -= OnViewerNavigationCompleted;
        ViewerWebView.CoreWebView2.WebMessageReceived -= OnViewerWebMessageReceived;
        ViewerWebView.CoreWebView2.WebResourceRequested -= OnViewerWebResourceRequested;
        _webViewEventsAttached = false;
        _isViewerReady = false;
    }

    private WeatherSnapshot CreateQueueWeatherStub()
    {
        var locationName = string.IsNullOrWhiteSpace(_currentLocationQuery) ? "Unknown" : _currentLocationQuery;
        return new WeatherSnapshot(
            LocationName: locationName,
            ShortForecast: "Unknown",
            DetailedForecast: string.Empty,
            TemperatureF: null,
            TemperatureC: null,
            Wind: string.Empty,
            RelativeHumidityPercent: null,
            IconUrl: null,
            TimeZoneId: null,
            Alerts: Array.Empty<WeatherAlertItem>(),
            CapturedAt: DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> NormalizePoiList(IReadOnlyList<string> pois)
    {
        if (pois.Count == 0)
        {
            return Array.Empty<string>();
        }

        var deduped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var poi in pois)
        {
            if (string.IsNullOrWhiteSpace(poi))
            {
                continue;
            }

            var trimmed = poi.Trim();
            var key = NormalizeTargetKey(trimmed);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!deduped.TryGetValue(key, out var existing) || IsPreferredPoiDisplayName(trimmed, existing))
            {
                deduped[key] = trimmed;
            }
        }

        return deduped.Values.ToArray();
    }

    private static bool IsPreferredPoiDisplayName(string candidate, string existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length != existing.Length)
        {
            return candidate.Length > existing.Length;
        }

        return string.Compare(candidate, existing, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static string NormalizeTargetKey(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            target
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeRelativePath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static string BuildViewerModelUrl(string localRelativePath)
    {
        var segments = NormalizeRelativePath(localRelativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString);
        var encodedPath = string.Join("/", segments);
        return $"https://{ViewerVirtualHostName}{CacheRoutePrefix}{encodedPath}";
    }

    private static string GetContentTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".bin" => "application/octet-stream",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ktx2" => "image/ktx2",
            ".basis" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text.Trim().Replace(Environment.NewLine, " ");
        return cleaned.Length <= maxLength ? cleaned : $"{cleaned[..maxLength]}...";
    }

    private void EmitDebug(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        DebugMessage?.Invoke(this, message);
    }

    private sealed record ModelListItem(
        string DisplayLabel,
        string LocalRelativePath,
        string ViewerModelUrl,
        string LocalWebUrl);

    private sealed class ModelTableRow : INotifyPropertyChanged
    {
        private bool _canDelete;
        private bool _canDownloadNow;
        private bool _canQueue;
        private bool _isDownloading;
        private bool _isQueued;
        private string _modelFileName;
        private string _poiKey;
        private string _poiName;
        private string _queueButtonText;
        private string _statusText;

        public ModelTableRow(
            string poiName,
            string poiKey,
            string modelFileName,
            string statusText,
            string localRelativePath,
            string viewerModelUrl,
            string localWebUrl,
            bool isCachedModel,
            bool canDelete)
        {
            _poiName = poiName;
            _poiKey = poiKey;
            _modelFileName = modelFileName;
            _statusText = statusText;
            LocalRelativePath = localRelativePath;
            ViewerModelUrl = viewerModelUrl;
            LocalWebUrl = localWebUrl;
            IsCachedModel = isCachedModel;
            _queueButtonText = "Add to Queue";
            _canDelete = canDelete;
            RefreshActionState(false);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string PoiName
        {
            get => _poiName;
            set => SetProperty(ref _poiName, value);
        }

        public string PoiKey
        {
            get => _poiKey;
            set => SetProperty(ref _poiKey, value);
        }

        public string ModelFileName
        {
            get => _modelFileName;
            set => SetProperty(ref _modelFileName, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string LocalRelativePath { get; }

        public string ViewerModelUrl { get; }

        public string LocalWebUrl { get; }

        public bool IsCachedModel { get; }

        public bool IsQueued
        {
            get => _isQueued;
            set => SetProperty(ref _isQueued, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetProperty(ref _isDownloading, value);
        }

        public bool CanQueue
        {
            get => _canQueue;
            private set => SetProperty(ref _canQueue, value);
        }

        public bool CanDownloadNow
        {
            get => _canDownloadNow;
            private set => SetProperty(ref _canDownloadNow, value);
        }

        public bool CanDelete
        {
            get => _canDelete;
            private set => SetProperty(ref _canDelete, value);
        }

        public string QueueButtonText
        {
            get => _queueButtonText;
            private set => SetProperty(ref _queueButtonText, value);
        }

        public string RowIdentity => IsCachedModel
            ? NormalizeRelativePath(LocalRelativePath)
            : $"missing::{PoiKey}";

        public void RefreshActionState(bool canQueueMeshy)
        {
            if (IsCachedModel)
            {
                QueueButtonText = "Cached";
                CanQueue = false;
                CanDownloadNow = false;
                CanDelete = true;
                return;
            }

            CanDelete = false;
            if (IsDownloading)
            {
                QueueButtonText = "Downloading";
                CanQueue = false;
                CanDownloadNow = false;
                return;
            }

            if (IsQueued)
            {
                QueueButtonText = "Queued";
                CanQueue = false;
                CanDownloadNow = false;
                return;
            }

            QueueButtonText = "Add to Queue";
            CanQueue = canQueueMeshy;
            CanDownloadNow = canQueueMeshy;
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Malie.Interop;
using Malie.Models;
using Malie.Services;
using Microsoft.Web.WebView2.Core;

namespace Malie.Windows;

public partial class SettingsWindow : Window
{
    private const string AngularShellVirtualHostName = "malie-ui.local";
    private const int MaxBufferedLogLines = 500;    private const int MaxMeshyLogLines = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event EventHandler<LocationRenderSettings>? SettingsSubmitted;
    public event EventHandler? UseCurrentLocationRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? MeshyCacheClearRequested;
    public event EventHandler? MeshyModelViewerRequested;
    public event EventHandler? MeshyManagerRefreshRequested;
    public event EventHandler<MeshyQueueRequest>? MeshyManagerQueueRequested;
    public event EventHandler<string>? MeshyManagerSetActiveRequested;
    public event EventHandler<string>? MeshyManagerDeleteRequested;
    public event EventHandler<MeshyRenameRequest>? MeshyManagerRenameRequested;
    public event EventHandler<string>? MeshyManagerCustomPromptRequested;
    public event EventHandler<double>? MeshyManagerRotationRequested;
    public event EventHandler<MeshyImportRequest>? MeshyManagerImportRequested;
    public event EventHandler<MeshyExportRequest>? MeshyManagerExportRequested;    public event EventHandler? OnboardingPoiRefreshRequested;
    public event EventHandler? ResetToOnboardingRequested;
    public event EventHandler<LogFilterSettings>? LogFilterChanged;
    public event EventHandler<bool>? DebugLogPaneVisibilityChanged;
    public event EventHandler? GlbOrientationViewRequested;
    public event EventHandler<GlbOrientationSettings>? GlbOrientationChanged;
    public event EventHandler<string>? WallpaperBackgroundColorChanged;
    public event EventHandler<string>? WallpaperMonitorChanged;
    public event EventHandler<WallpaperBackgroundImageChangeRequest>? WallpaperBackgroundImageChanged;
    public event EventHandler? WallpaperBackgroundImageCleared;
    public event EventHandler<string>? WallpaperBackgroundDisplayModeChanged;
    public event EventHandler<bool>? WallpaperAnimatedBackgroundChanged;
    public event EventHandler<bool>? WallpaperStatsOverlayChanged;
    public event EventHandler<WallpaperTextStyleSettings>? WallpaperTextStyleChanged;

    private readonly Queue<string> _debugLines = new();    private readonly Queue<string> _pendingHostMessages = new();
    private string _locationQuery;
    private string _meshyApiKey;
    private string _weatherApiKey;
    private string _latLngApiKey;    private TemperatureScale _temperatureScale;
    private string _wallpaperBackgroundColor = "#7AA7D8";
    private string _wallpaperMonitorDeviceName = string.Empty;
    private string _wallpaperBackgroundImageFileName = string.Empty;
    private string _wallpaperBackgroundDisplayMode = "Fill";
    private bool _useAnimatedAiBackground;
    private bool _showWallpaperStatsOverlay = true;
    private WallpaperTextStyleSettings _wallpaperTextStyle = WallpaperTextStyleSettings.Default;
    private readonly IReadOnlyList<string> _systemFonts = Fonts.SystemFontFamilies
        .Select(family => family.Source?.Trim() ?? string.Empty)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private bool _showDebugLogPane;
    private LogFilterSettings _logFilters;
    private GlbOrientationSettings _glbOrientation;
    private string _statusText = "Waiting for first update...";
    private DateTimeOffset? _lastUpdated;
    private bool _webViewReady;    private IReadOnlyList<string> _onboardingPois = Array.Empty<string>();
    private bool _meshyManagerBusy;
    private string _meshyManagerStatus = "Ready.";
    private string _meshyQueueStatus = "Queue idle.";
    private double _meshyRotationMinutes;
    private double _meshyProgressPercent;
    private string _meshyProgressText = "Queue idle.";
    private IReadOnlyList<MeshyModelRowState> _meshyRows = Array.Empty<MeshyModelRowState>();
    private readonly Queue<string> _meshyLogLines = new();
    private IReadOnlyList<DesktopWallpaperHost.DisplayMonitorInfo> _availableWallpaperMonitors = Array.Empty<DesktopWallpaperHost.DisplayMonitorInfo>();

    public SettingsWindow(
        string initialLocation,
        string initialMeshyApiKey,
        string initialWeatherApiKey,
        string initialLatLngApiKey,
        TemperatureScale initialTemperatureScale,
        string initialWallpaperMonitorDeviceName,
        string initialWallpaperBackgroundColor,
        string initialWallpaperBackgroundImageFileName,
        string initialWallpaperBackgroundDisplayMode,
        bool initialUseAnimatedAiBackground,
        bool initialShowWallpaperStatsOverlay,
        WallpaperTextStyleSettings initialWallpaperTextStyle,
        bool initialShowDebugLogPane,
        LogFilterSettings initialLogFilters,
        GlbOrientationSettings initialGlbOrientation)
    {
        _locationQuery = initialLocation;
        _meshyApiKey = initialMeshyApiKey;
        _weatherApiKey = initialWeatherApiKey;
        _latLngApiKey = initialLatLngApiKey;        _temperatureScale = initialTemperatureScale;
        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
        _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(initialWallpaperMonitorDeviceName);
        _wallpaperBackgroundColor = NormalizeHexColor(initialWallpaperBackgroundColor, "#7AA7D8");
        _wallpaperBackgroundImageFileName = NormalizeImageFileName(initialWallpaperBackgroundImageFileName);
        _wallpaperBackgroundDisplayMode = NormalizeWallpaperBackgroundDisplayMode(initialWallpaperBackgroundDisplayMode);
        _useAnimatedAiBackground = initialUseAnimatedAiBackground;
        _showWallpaperStatsOverlay = initialShowWallpaperStatsOverlay;
        _wallpaperTextStyle = (initialWallpaperTextStyle ?? WallpaperTextStyleSettings.Default).Normalize();
        _showDebugLogPane = initialShowDebugLogPane;
        _logFilters = initialLogFilters;
        _glbOrientation = initialGlbOrientation.Normalize();

        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetStatus(string statusText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(statusText));
            return;
        }

        _statusText = statusText;
        SendHostMessage(new
        {
            type = "host.status",
            payload = new
            {
                text = _statusText
            }
        });
    }

    public void SetLastUpdated(DateTimeOffset updatedAt)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetLastUpdated(updatedAt));
            return;
        }

        _lastUpdated = updatedAt;
        SendHostMessage(new
        {
            type = "host.lastUpdated",
            payload = new
            {
                iso = updatedAt.ToString("o"),
                display = updatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            }
        });
    }

    public void SetLocation(string location)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetLocation(location));
            return;
        }

        _locationQuery = location;
        SendHostMessage(new
        {
            type = "host.location",
            payload = new
            {
                location = _locationQuery
            }
        });
    }

    public void SetDebugLines(IEnumerable<string> lines)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetDebugLines(lines));
            return;
        }

        _debugLines.Clear();
        foreach (var line in lines.TakeLast(MaxBufferedLogLines))
        {
            _debugLines.Enqueue(line);
        }

        SendHostMessage(new
        {
            type = "host.logReplace",
            payload = new
            {
                lines = _debugLines.ToArray()
            }
        });
    }

    public void AppendDebugLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendDebugLine(line));
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _debugLines.Enqueue(line);
        while (_debugLines.Count > MaxBufferedLogLines)
        {
            _debugLines.Dequeue();
        }

        SendHostMessage(new
        {
            type = "host.logAppend",
            payload = new
            {
                line
            }
        });
    }

    public void SetDebugLogPaneVisible(bool visible)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetDebugLogPaneVisible(visible));
            return;
        }

        _showDebugLogPane = visible;
        SendHostMessage(new
        {
            type = "host.debugPaneVisibility",
            payload = new
            {
                visible = _showDebugLogPane
            }
        });
    }

    public void SetGlbOrientation(GlbOrientationSettings orientation)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetGlbOrientation(orientation));
            return;
        }

        _glbOrientation = orientation.Normalize();
        SendHostMessage(new
        {
            type = "host.glbOrientation",
            payload = BuildGlbOrientationPayload(_glbOrientation)
        });
    }

    public void SetWallpaperBackgroundColor(string color)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperBackgroundColor(color));
            return;
        }

        _wallpaperBackgroundColor = NormalizeHexColor(color, _wallpaperBackgroundColor);
        SendHostMessage(new
        {
            type = "host.wallpaperBackgroundColor",
            payload = new
            {
                wallpaperBackgroundColor = _wallpaperBackgroundColor
            }
        });
    }

    public void SetWallpaperMonitorDeviceName(string monitorDeviceName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperMonitorDeviceName(monitorDeviceName));
            return;
        }

        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
        _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(monitorDeviceName);

        SendHostMessage(new
        {
            type = "host.wallpaperMonitor",
            payload = new
            {
                wallpaperMonitorDeviceName = _wallpaperMonitorDeviceName,
                availableMonitors = BuildWallpaperMonitorPayload(_availableWallpaperMonitors)
            }
        });
    }

    public void SetWallpaperBackgroundImageFileName(string fileName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperBackgroundImageFileName(fileName));
            return;
        }

        _wallpaperBackgroundImageFileName = NormalizeImageFileName(fileName);
        SendHostMessage(new
        {
            type = "host.wallpaperBackgroundImage",
            payload = new
            {
                wallpaperBackgroundImageFileName = _wallpaperBackgroundImageFileName
            }
        });
    }

    public void SetWallpaperBackgroundDisplayMode(string mode)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperBackgroundDisplayMode(mode));
            return;
        }

        _wallpaperBackgroundDisplayMode = NormalizeWallpaperBackgroundDisplayMode(mode);
        SendHostMessage(new
        {
            type = "host.wallpaperBackgroundDisplayMode",
            payload = new
            {
                wallpaperBackgroundDisplayMode = _wallpaperBackgroundDisplayMode
            }
        });
    }

    public void SetWallpaperAnimatedBackgroundEnabled(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperAnimatedBackgroundEnabled(enabled));
            return;
        }

        _useAnimatedAiBackground = enabled;
        SendHostMessage(new
        {
            type = "host.wallpaperAnimatedBackground",
            payload = new
            {
                useAnimatedAiBackground = _useAnimatedAiBackground
            }
        });
    }

    public void SetWallpaperStatsOverlayVisible(bool visible)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperStatsOverlayVisible(visible));
            return;
        }

        _showWallpaperStatsOverlay = visible;
        SendHostMessage(new
        {
            type = "host.wallpaperStatsOverlay",
            payload = new
            {
                showWallpaperStatsOverlay = _showWallpaperStatsOverlay
            }
        });
    }

    public void SetWallpaperTextStyle(WallpaperTextStyleSettings textStyle)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetWallpaperTextStyle(textStyle));
            return;
        }

        _wallpaperTextStyle = (textStyle ?? WallpaperTextStyleSettings.Default).Normalize();
        SendHostMessage(new
        {
            type = "host.wallpaperTextStyle",
            payload = BuildWallpaperTextStylePayload(_wallpaperTextStyle)
        });
    }
    public void SetMeshyManagerState(MeshyManagerStatePayload state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetMeshyManagerState(state));
            return;
        }

        _meshyManagerStatus = state.Status?.Trim() ?? string.Empty;
        _meshyQueueStatus = state.QueueStatus?.Trim() ?? string.Empty;
        _meshyManagerBusy = state.IsBusy;
        _meshyRotationMinutes = state.RotationMinutes;
        _meshyProgressPercent = Math.Max(0, Math.Min(100, state.ProgressPercent));
        _meshyProgressText = state.ProgressText?.Trim() ?? string.Empty;
        _meshyRows = state.Rows?.ToArray() ?? Array.Empty<MeshyModelRowState>();

        _meshyLogLines.Clear();
        if (state.Logs is not null)
        {
            foreach (var line in state.Logs.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(MaxMeshyLogLines))
            {
                _meshyLogLines.Enqueue(line);
            }
        }

        SendHostMessage(new
        {
            type = "host.meshyManager",
            payload = BuildMeshyManagerPayload()
        });
    }

    public void AppendMeshyManagerLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendMeshyManagerLog(line));
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _meshyLogLines.Enqueue(line);
        while (_meshyLogLines.Count > MaxMeshyLogLines)
        {
            _meshyLogLines.Dequeue();
        }

        SendHostMessage(new
        {
            type = "host.meshyManagerLog",
            payload = new
            {
                line
            }
        });
    }

    private object BuildMeshyManagerPayload()
    {
        return new
        {
            status = _meshyManagerStatus,
            queueStatus = _meshyQueueStatus,
            isBusy = _meshyManagerBusy,
            rotationMinutes = _meshyRotationMinutes,
            progressPercent = _meshyProgressPercent,
            progressText = _meshyProgressText,
            rows = _meshyRows.Select(row => new
            {
                poiKey = row.PoiKey,
                poiName = row.PoiName,
                modelFileName = row.ModelFileName,
                statusText = row.StatusText,
                statusKind = row.StatusKind,
                isCachedModel = row.IsCachedModel,
                isActiveModel = row.IsActiveModel,
                canQueue = row.CanQueue,
                canDownloadNow = row.CanDownloadNow,
                canDelete = row.CanDelete,
                localRelativePath = row.LocalRelativePath
            }),
            logs = _meshyLogLines.ToArray()
        };
    }

    public void ShowHelpGuide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowHelpGuide);
            return;
        }

        SendHostMessage(new
        {
            type = "host.openHelp",
            payload = new { }
        });
    }
    public void SetOnboardingPois(IReadOnlyList<string> pois)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetOnboardingPois(pois));
            return;
        }

        _onboardingPois = pois
            .Where(poi => !string.IsNullOrWhiteSpace(poi))
            .Select(poi => poi.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SendHostMessage(new
        {
            type = "host.onboardingPois",
            payload = new
            {
                pois = _onboardingPois
            }
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeShellAsync();
    }

    private async Task InitializeShellAsync()
    {
        try
        {
            await ShellWebView.EnsureCoreWebView2Async();
            ShellWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ShellWebView.NavigationCompleted += OnNavigationCompleted;
            ShellWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            ShellWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            ShellWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            var shellRoot = Path.Combine(AppContext.BaseDirectory, "ui-shell", "dist", "ui-shell", "browser");
            var shellIndexPath = Path.Combine(shellRoot, "index.html");
            if (!Directory.Exists(shellRoot) || !File.Exists(shellIndexPath))
            {
                SetStatus("Angular UI build not found. Run `npm run build` in ui-shell.");
                ShowFallbackHtml(
                    $"<h2>Angular UI missing</h2><p>Expected build at:</p><pre>{shellIndexPath}</pre><p>Run <code>npm run build</code> in <code>ui-shell</code>.</p>");
                return;
            }

            ShellWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AngularShellVirtualHostName,
                shellRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            ShellWebView.Source = new Uri($"https://{AngularShellVirtualHostName}/index.html");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to initialize Angular shell: {ex.Message}");
            ShowFallbackHtml($"<h2>Shell initialization failed</h2><pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>");
        }
    }

    private void ShowFallbackHtml(string bodyHtml)
    {
        var html =
            "<!doctype html>" +
            "<html lang=\"en\">" +
            "<head>" +
            "<meta charset=\"utf-8\" />" +
            "<title>Mâlie</title>" +
            "<style>" +
            "body { font-family: Segoe UI, Arial, sans-serif; padding: 1.5rem; background: #0b1020; color: #e4ecff; }" +
            "pre { white-space: pre-wrap; background: #0f172a; padding: 0.75rem; border-radius: 8px; }" +
            "code { color: #a5f3fc; }" +
            "</style>" +
            "</head>" +
            "<body>" +
            bodyHtml +
            "</body>" +
            "</html>";
        ShellWebView.NavigateToString(html);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            SetStatus($"Angular shell navigation failed: {e.WebErrorStatus}");
            return;
        }

        _webViewReady = true;
        await FlushPendingHostMessagesAsync();
        SendFullStateSnapshot();    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : default;

            switch (type)
            {
                case "settings.requestState":
                    SendFullStateSnapshot();
                    break;
                case "settings.apply":
                    HandleApply(payload, refreshAfterApply: false);
                    break;
                case "settings.refresh":
                    HandleApply(payload, refreshAfterApply: true);
                    break;
                case "settings.useCurrentLocation":
                    UseCurrentLocationRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.resetApplication":
                    HandleResetRequest();
                    break;
                case "settings.openMeshyModels":
                    MeshyModelViewerRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.onboarding.refreshPois":
                    OnboardingPoiRefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.meshy.refresh":
                    MeshyManagerRefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.meshy.queue":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var poiName = GetString(payload, "poiName");
                        if (!string.IsNullOrWhiteSpace(poiName))
                        {
                            var prioritize = GetBool(payload, "prioritize", false);
                            MeshyManagerQueueRequested?.Invoke(this, new MeshyQueueRequest(poiName, prioritize));
                        }
                    }
                    break;
                case "settings.meshy.delete":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var localRelativePath = GetString(payload, "localRelativePath");
                        if (!string.IsNullOrWhiteSpace(localRelativePath))
                        {
                            MeshyManagerDeleteRequested?.Invoke(this, localRelativePath);
                        }
                    }
                    break;
                case "settings.meshy.setActive":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var localRelativePath = GetString(payload, "localRelativePath");
                        if (!string.IsNullOrWhiteSpace(localRelativePath))
                        {
                            MeshyManagerSetActiveRequested?.Invoke(this, localRelativePath);
                        }
                    }
                    break;
                case "settings.meshy.rename":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var oldName = GetString(payload, "oldName");
                        var newName = GetString(payload, "newName");
                        var localRelativePath = GetString(payload, "localRelativePath");
                        if (!string.IsNullOrWhiteSpace(oldName) &&
                            !string.IsNullOrWhiteSpace(newName) &&
                            !string.IsNullOrWhiteSpace(localRelativePath))
                        {
                            MeshyManagerRenameRequested?.Invoke(
                                this,
                                new MeshyRenameRequest(oldName, newName, localRelativePath));
                        }
                    }
                    break;
                case "settings.meshy.customPrompt":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var prompt = GetString(payload, "prompt");
                        if (!string.IsNullOrWhiteSpace(prompt))
                        {
                            MeshyManagerCustomPromptRequested?.Invoke(this, prompt);
                        }
                    }
                    break;
                case "settings.meshy.rotation":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var minutes = GetDouble(payload, "minutes", 0);
                        MeshyManagerRotationRequested?.Invoke(this, minutes);
                    }
                    break;
                case "settings.meshy.import":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var poiName = GetString(payload, "poiName");
                        var fileName = GetString(payload, "fileName");
                        var dataUrl = GetString(payload, "dataUrl");
                        if (!string.IsNullOrWhiteSpace(poiName) &&
                            !string.IsNullOrWhiteSpace(fileName) &&
                            !string.IsNullOrWhiteSpace(dataUrl))
                        {
                            MeshyManagerImportRequested?.Invoke(
                                this,
                                new MeshyImportRequest(poiName, fileName, dataUrl));
                        }
                    }
                    break;
                case "settings.meshy.export":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var localRelativePath = GetString(payload, "localRelativePath");
                        if (!string.IsNullOrWhiteSpace(localRelativePath))
                        {
                            MeshyManagerExportRequested?.Invoke(
                                this,
                                new MeshyExportRequest(localRelativePath));
                        }
                    }
                    break;
                case "settings.openGlbOrientation":
                    GlbOrientationViewRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.glbOrientationChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var liveOrientation = ParseGlbOrientation(payload, _glbOrientation).Normalize();
                        _glbOrientation = liveOrientation;
                        GlbOrientationChanged?.Invoke(this, liveOrientation);
                        SendHostMessage(new
                        {
                            type = "host.glbOrientation",
                            payload = BuildGlbOrientationPayload(_glbOrientation)
                        });
                    }
                    break;
                case "settings.wallpaperBackgroundColorChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var candidate = GetString(payload, "wallpaperBackgroundColor");
                        var normalized = NormalizeHexColor(candidate, _wallpaperBackgroundColor);
                        if (!string.Equals(normalized, _wallpaperBackgroundColor, StringComparison.OrdinalIgnoreCase))
                        {
                            _wallpaperBackgroundColor = normalized;
                            WallpaperBackgroundColorChanged?.Invoke(this, _wallpaperBackgroundColor);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperBackgroundColor",
                            payload = new
                            {
                                wallpaperBackgroundColor = _wallpaperBackgroundColor
                            }
                        });
                    }
                    break;
                case "settings.wallpaperMonitorChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
                        var normalized = NormalizeWallpaperMonitorDeviceName(
                            GetString(payload, "wallpaperMonitorDeviceName"));
                        if (!string.Equals(normalized, _wallpaperMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            _wallpaperMonitorDeviceName = normalized;
                            WallpaperMonitorChanged?.Invoke(this, _wallpaperMonitorDeviceName);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperMonitor",
                            payload = new
                            {
                                wallpaperMonitorDeviceName = _wallpaperMonitorDeviceName,
                                availableMonitors = BuildWallpaperMonitorPayload(_availableWallpaperMonitors)
                            }
                        });
                    }
                    break;
                case "settings.wallpaperBackgroundImageChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var fileName = GetString(payload, "fileName");
                        var dataUrl = GetString(payload, "dataUrl");
                        if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(dataUrl))
                        {
                            WallpaperBackgroundImageChanged?.Invoke(
                                this,
                                new WallpaperBackgroundImageChangeRequest(fileName, dataUrl));
                        }
                    }
                    break;
                case "settings.wallpaperBackgroundImageCleared":
                    WallpaperBackgroundImageCleared?.Invoke(this, EventArgs.Empty);
                    break;
                case "settings.wallpaperBackgroundDisplayModeChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var mode = NormalizeWallpaperBackgroundDisplayMode(
                            GetString(payload, "wallpaperBackgroundDisplayMode"));
                        if (!string.Equals(mode, _wallpaperBackgroundDisplayMode, StringComparison.OrdinalIgnoreCase))
                        {
                            _wallpaperBackgroundDisplayMode = mode;
                            WallpaperBackgroundDisplayModeChanged?.Invoke(this, _wallpaperBackgroundDisplayMode);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperBackgroundDisplayMode",
                            payload = new
                            {
                                wallpaperBackgroundDisplayMode = _wallpaperBackgroundDisplayMode
                            }
                        });
                    }
                    break;
                case "settings.wallpaperAnimatedBackgroundChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var enabled = GetBool(payload, "useAnimatedAiBackground", _useAnimatedAiBackground);
                        if (enabled != _useAnimatedAiBackground)
                        {
                            _useAnimatedAiBackground = enabled;
                            WallpaperAnimatedBackgroundChanged?.Invoke(this, _useAnimatedAiBackground);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperAnimatedBackground",
                            payload = new
                            {
                                useAnimatedAiBackground = _useAnimatedAiBackground
                            }
                        });
                    }
                    break;
                case "settings.wallpaperStatsOverlayChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var visible = GetBool(payload, "showWallpaperStatsOverlay", _showWallpaperStatsOverlay);
                        if (visible != _showWallpaperStatsOverlay)
                        {
                            _showWallpaperStatsOverlay = visible;
                            WallpaperStatsOverlayChanged?.Invoke(this, _showWallpaperStatsOverlay);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperStatsOverlay",
                            payload = new
                            {
                                showWallpaperStatsOverlay = _showWallpaperStatsOverlay
                            }
                        });
                    }
                    break;
                case "settings.wallpaperTextStyleChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        var styleElement = payload.TryGetProperty("wallpaperTextStyle", out var nestedStyleElement) &&
                                           nestedStyleElement.ValueKind == JsonValueKind.Object
                            ? nestedStyleElement
                            : payload;
                        var nextStyle = ParseWallpaperTextStyle(styleElement, _wallpaperTextStyle).Normalize();
                        if (nextStyle != _wallpaperTextStyle)
                        {
                            _wallpaperTextStyle = nextStyle;
                            WallpaperTextStyleChanged?.Invoke(this, _wallpaperTextStyle);
                        }

                        SendHostMessage(new
                        {
                            type = "host.wallpaperTextStyle",
                            payload = BuildWallpaperTextStylePayload(_wallpaperTextStyle)
                        });
                    }
                    break;
                case "settings.logFiltersChanged":
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        _logFilters = ParseLogFilters(payload, _logFilters);
                        LogFilterChanged?.Invoke(this, _logFilters);
                    }
                    break;
                case "settings.openHelp":
                    ShowHelpGuide();
                    break;
                case "settings.clearMeshyCache":
                    MeshyCacheClearRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to process UI message: {ex.Message}");
        }
    }

    private void HandleApply(JsonElement payload, bool refreshAfterApply)
    {
        var settings = BuildSettingsFromPayload(payload);
        if (settings is null)
        {
            return;
        }

        SettingsSubmitted?.Invoke(this, settings);
        if (refreshAfterApply)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    private void HandleResetRequest()
    {
        var confirmation = System.Windows.MessageBox.Show(
            this,
            "Reset the application and delete all local data and cached Meshy models? The app will close after reset.",
            "Reset Application",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation == MessageBoxResult.Yes)
        {
            ResetToOnboardingRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private LocationRenderSettings? BuildSettingsFromPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            SetStatus("Invalid settings payload from UI.");
            return null;
        }

        var location = GetString(payload, "location");
        if (string.IsNullOrWhiteSpace(location))
        {
            SetStatus("Location cannot be empty.");
            return null;
        }

        var meshyApiKey = GetString(payload, "meshyApiKey");
        var weatherApiKey = GetString(payload, "weatherApiKey");
        var latLngApiKey = GetString(payload, "latLngApiKey");
        if (string.IsNullOrWhiteSpace(meshyApiKey))
        {
            SetStatus("Meshy API key is required.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(weatherApiKey))
        {
            SetStatus("Weather API key is required.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(latLngApiKey))
        {
            SetStatus("LatLng API key is required.");
            return null;
        }

        _locationQuery = location.Trim();
        _meshyApiKey = meshyApiKey.Trim();
        _weatherApiKey = weatherApiKey.Trim();
        _latLngApiKey = latLngApiKey.Trim();
        _temperatureScale = ParseTemperatureScale(GetString(payload, "temperatureScale"));
        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
        _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(
            GetString(payload, "wallpaperMonitorDeviceName"));
        _wallpaperBackgroundColor = NormalizeHexColor(
            GetString(payload, "wallpaperBackgroundColor"),
            _wallpaperBackgroundColor);
        _wallpaperBackgroundImageFileName = NormalizeImageFileName(
            GetString(payload, "wallpaperBackgroundImageFileName"));
        _wallpaperBackgroundDisplayMode = NormalizeWallpaperBackgroundDisplayMode(
            GetString(payload, "wallpaperBackgroundDisplayMode"));
        _useAnimatedAiBackground = GetBool(payload, "useAnimatedAiBackground", _useAnimatedAiBackground);
        _showWallpaperStatsOverlay = GetBool(payload, "showWallpaperStatsOverlay", _showWallpaperStatsOverlay);
        if (payload.TryGetProperty("wallpaperTextStyle", out var textStyleElement) &&
            textStyleElement.ValueKind == JsonValueKind.Object)
        {
            _wallpaperTextStyle = ParseWallpaperTextStyle(textStyleElement, _wallpaperTextStyle).Normalize();
        }
        _showDebugLogPane = GetBool(payload, "showDebugLogPane", _showDebugLogPane);

        if (payload.TryGetProperty("logFilters", out var filtersElement) &&
            filtersElement.ValueKind == JsonValueKind.Object)
        {
            _logFilters = ParseLogFilters(filtersElement, _logFilters);
            LogFilterChanged?.Invoke(this, _logFilters);
        }

        if (payload.TryGetProperty("glbOrientation", out var orientationElement) &&
            orientationElement.ValueKind == JsonValueKind.Object)
        {
            _glbOrientation = ParseGlbOrientation(orientationElement, _glbOrientation).Normalize();
        }

        DebugLogPaneVisibilityChanged?.Invoke(this, _showDebugLogPane);
        WallpaperStatsOverlayChanged?.Invoke(this, _showWallpaperStatsOverlay);

        return new LocationRenderSettings(
            _locationQuery,
            _meshyApiKey,
            _weatherApiKey,
            _latLngApiKey,            _temperatureScale,
            _wallpaperMonitorDeviceName,
            _wallpaperBackgroundColor,
            _wallpaperBackgroundImageFileName,
            _wallpaperBackgroundDisplayMode,
            _useAnimatedAiBackground,
            _wallpaperTextStyle,
            _showWallpaperStatsOverlay,
            _showDebugLogPane,
            _logFilters,
            _glbOrientation);
    }

    private void SendFullStateSnapshot()
    {
        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
        _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(_wallpaperMonitorDeviceName);

        SendHostMessage(new
        {
            type = "host.state",
            payload = new
            {
                location = _locationQuery,
                meshyApiKey = _meshyApiKey,
                weatherApiKey = _weatherApiKey,
                latLngApiKey = _latLngApiKey,
                temperatureScale = _temperatureScale.ToString(),
                wallpaperMonitorDeviceName = _wallpaperMonitorDeviceName,
                availableMonitors = BuildWallpaperMonitorPayload(_availableWallpaperMonitors),
                wallpaperBackgroundColor = _wallpaperBackgroundColor,
                wallpaperBackgroundImageFileName = _wallpaperBackgroundImageFileName,
                wallpaperBackgroundDisplayMode = _wallpaperBackgroundDisplayMode,
                useAnimatedAiBackground = _useAnimatedAiBackground,
                showWallpaperStatsOverlay = _showWallpaperStatsOverlay,
                wallpaperTextStyle = BuildWallpaperTextStylePayload(_wallpaperTextStyle),
                systemFonts = _systemFonts,
                showDebugLogPane = _showDebugLogPane,
                logFilters = BuildLogFiltersPayload(_logFilters),
                glbOrientation = BuildGlbOrientationPayload(_glbOrientation),                meshyManager = BuildMeshyManagerPayload(),
                onboardingPois = _onboardingPois,
                status = _statusText,
                lastUpdatedIso = _lastUpdated?.ToString("o"),
                lastUpdatedDisplay = _lastUpdated?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                logs = _debugLines.ToArray()
            }
        });
    }

    private object BuildLogFiltersPayload(LogFilterSettings filters)
    {
        return new
        {
            showSystem = filters.ShowSystem,
            showWeather = filters.ShowWeather,
            showLatLng = filters.ShowLatLng,            showMeshy = filters.ShowMeshy,
            showHighDetail = filters.ShowHighDetail,
            showRendererDebug = filters.ShowRendererDebug,
            showErrors = filters.ShowErrors
        };
    }

    private object BuildWallpaperMonitorPayload(IReadOnlyList<DesktopWallpaperHost.DisplayMonitorInfo> monitors)
    {
        return monitors.Select(monitor => new
        {
            deviceName = monitor.DeviceName,
            label = monitor.Label,
            isPrimary = monitor.IsPrimary,
            x = monitor.X,
            y = monitor.Y,
            width = monitor.Width,
            height = monitor.Height
        });
    }

    private static object BuildGlbOrientationPayload(GlbOrientationSettings orientation)
    {
        return new
        {
            rotationXDegrees = orientation.RotationXDegrees,
            rotationYDegrees = orientation.RotationYDegrees,
            rotationZDegrees = orientation.RotationZDegrees,
            scale = orientation.Scale,
            offsetX = orientation.OffsetX,
            offsetY = orientation.OffsetY,
            offsetZ = orientation.OffsetZ
        };
    }

    private static object BuildWallpaperTextStylePayload(WallpaperTextStyleSettings textStyle)
    {
        var normalized = (textStyle ?? WallpaperTextStyleSettings.Default).Normalize();
        return new
        {
            timeFontFamily = normalized.TimeFontFamily,
            locationFontFamily = normalized.LocationFontFamily,
            dateFontFamily = normalized.DateFontFamily,
            temperatureFontFamily = normalized.TemperatureFontFamily,
            summaryFontFamily = normalized.SummaryFontFamily,
            poiFontFamily = normalized.PoiFontFamily,
            alertsFontFamily = normalized.AlertsFontFamily,
            timeFontSize = normalized.TimeFontSize,
            locationFontSize = normalized.LocationFontSize,
            dateFontSize = normalized.DateFontSize,
            temperatureFontSize = normalized.TemperatureFontSize,
            summaryFontSize = normalized.SummaryFontSize,
            poiFontSize = normalized.PoiFontSize,
            alertsFontSize = normalized.AlertsFontSize
        };
    }

    private void SendHostMessage(object message)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        if (!_webViewReady || ShellWebView.CoreWebView2 is null)
        {
            _pendingHostMessages.Enqueue(json);
            return;
        }

        _ = DispatchHostMessageAsync(json);
    }

    private async Task FlushPendingHostMessagesAsync()
    {
        while (_pendingHostMessages.Count > 0)
        {
            var json = _pendingHostMessages.Dequeue();
            await DispatchHostMessageAsync(json);
        }
    }

    private async Task DispatchHostMessageAsync(string messageJson)
    {
        if (ShellWebView.CoreWebView2 is null)
        {
            return;
        }

        var script = $"window.__isometricLiveWeatherHost?.receive({messageJson});";
        await ShellWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static LogFilterSettings ParseLogFilters(JsonElement element, LogFilterSettings fallback)
    {
        return new LogFilterSettings(
            ShowSystem: GetBool(element, "showSystem", fallback.ShowSystem),
            ShowWeather: GetBool(element, "showWeather", fallback.ShowWeather),
            ShowLatLng: GetBool(element, "showLatLng", fallback.ShowLatLng),            ShowMeshy: GetBool(element, "showMeshy", fallback.ShowMeshy),
            ShowHighDetail: GetBool(element, "showHighDetail", fallback.ShowHighDetail),
            ShowRendererDebug: GetBool(element, "showRendererDebug", fallback.ShowRendererDebug),
            ShowErrors: GetBool(element, "showErrors", fallback.ShowErrors));
    }

    private static GlbOrientationSettings ParseGlbOrientation(JsonElement element, GlbOrientationSettings fallback)
    {
        return new GlbOrientationSettings(
            RotationXDegrees: GetDouble(element, "rotationXDegrees", fallback.RotationXDegrees),
            RotationYDegrees: GetDouble(element, "rotationYDegrees", fallback.RotationYDegrees),
            RotationZDegrees: GetDouble(element, "rotationZDegrees", fallback.RotationZDegrees),
            Scale: GetDouble(element, "scale", fallback.Scale),
            OffsetX: GetDouble(element, "offsetX", fallback.OffsetX),
            OffsetY: GetDouble(element, "offsetY", fallback.OffsetY),
            OffsetZ: GetDouble(element, "offsetZ", fallback.OffsetZ));
    }

    private static WallpaperTextStyleSettings ParseWallpaperTextStyle(
        JsonElement element,
        WallpaperTextStyleSettings fallback)
    {
        var source = (fallback ?? WallpaperTextStyleSettings.Default).Normalize();
        var legacyFontFamilyRaw = GetString(element, "fontFamily");
        var timeFontFamilyRaw = GetString(element, "timeFontFamily");
        var locationFontFamilyRaw = GetString(element, "locationFontFamily");
        var dateFontFamilyRaw = GetString(element, "dateFontFamily");
        var temperatureFontFamilyRaw = GetString(element, "temperatureFontFamily");
        var summaryFontFamilyRaw = GetString(element, "summaryFontFamily");
        var poiFontFamilyRaw = GetString(element, "poiFontFamily");
        var alertsFontFamilyRaw = GetString(element, "alertsFontFamily");
        return new WallpaperTextStyleSettings(
            TimeFontFamily: ResolveFontFamily(timeFontFamilyRaw, legacyFontFamilyRaw, source.TimeFontFamily),
            LocationFontFamily: ResolveFontFamily(locationFontFamilyRaw, legacyFontFamilyRaw, source.LocationFontFamily),
            DateFontFamily: ResolveFontFamily(dateFontFamilyRaw, legacyFontFamilyRaw, source.DateFontFamily),
            TemperatureFontFamily: ResolveFontFamily(temperatureFontFamilyRaw, legacyFontFamilyRaw, source.TemperatureFontFamily),
            SummaryFontFamily: ResolveFontFamily(summaryFontFamilyRaw, legacyFontFamilyRaw, source.SummaryFontFamily),
            PoiFontFamily: ResolveFontFamily(poiFontFamilyRaw, legacyFontFamilyRaw, source.PoiFontFamily),
            AlertsFontFamily: ResolveFontFamily(alertsFontFamilyRaw, legacyFontFamilyRaw, source.AlertsFontFamily),
            TimeFontSize: NormalizeFontSize(GetDouble(element, "timeFontSize", source.TimeFontSize), source.TimeFontSize),
            LocationFontSize: NormalizeFontSize(GetDouble(element, "locationFontSize", source.LocationFontSize), source.LocationFontSize),
            DateFontSize: NormalizeFontSize(GetDouble(element, "dateFontSize", source.DateFontSize), source.DateFontSize),
            TemperatureFontSize: NormalizeFontSize(GetDouble(element, "temperatureFontSize", source.TemperatureFontSize), source.TemperatureFontSize),
            SummaryFontSize: NormalizeFontSize(GetDouble(element, "summaryFontSize", source.SummaryFontSize), source.SummaryFontSize),
            PoiFontSize: NormalizeFontSize(GetDouble(element, "poiFontSize", source.PoiFontSize), source.PoiFontSize),
            AlertsFontSize: NormalizeFontSize(GetDouble(element, "alertsFontSize", source.AlertsFontSize), source.AlertsFontSize)).Normalize();
    }

    private static string ResolveFontFamily(string? lineFontFamily, string? legacyFontFamily, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(lineFontFamily))
        {
            return WallpaperTextStyleSettings.NormalizeFontFamily(lineFontFamily);
        }

        if (!string.IsNullOrWhiteSpace(legacyFontFamily))
        {
            return WallpaperTextStyleSettings.NormalizeFontFamily(legacyFontFamily);
        }

        return WallpaperTextStyleSettings.NormalizeFontFamily(fallback);
    }

    private static TemperatureScale ParseTemperatureScale(string value)
    {
        if (value.Equals(nameof(TemperatureScale.Celsius), StringComparison.OrdinalIgnoreCase))
        {
            return TemperatureScale.Celsius;
        }

        return TemperatureScale.Fahrenheit;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string TruncateForStatus(string text, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compact = text.Trim().Replace(Environment.NewLine, " ");
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}...";
    }

    private static bool GetBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static double GetDouble(JsonElement element, string propertyName, double fallback)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private string NormalizeWallpaperMonitorDeviceName(string? candidate)
    {
        var normalized = DesktopWallpaperHost.NormalizeMonitorDeviceName(candidate);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        _availableWallpaperMonitors = DesktopWallpaperHost.GetDisplayMonitors();
        normalized = DesktopWallpaperHost.NormalizeMonitorDeviceName(candidate);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    private static string NormalizeHexColor(string? value, string fallback)
    {
        var resolvedFallback = string.IsNullOrWhiteSpace(fallback) ? "#7AA7D8" : fallback.Trim();
        if (!resolvedFallback.StartsWith('#'))
        {
            resolvedFallback = $"#{resolvedFallback}";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return resolvedFallback.ToUpperInvariant();
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = $"#{trimmed}";
        }

        if (trimmed.Length == 4 &&
            IsHexChar(trimmed[1]) &&
            IsHexChar(trimmed[2]) &&
            IsHexChar(trimmed[3]))
        {
            return string.Concat(
                "#",
                trimmed[1], trimmed[1],
                trimmed[2], trimmed[2],
                trimmed[3], trimmed[3]).ToUpperInvariant();
        }

        if (trimmed.Length == 7 &&
            IsHexChar(trimmed[1]) &&
            IsHexChar(trimmed[2]) &&
            IsHexChar(trimmed[3]) &&
            IsHexChar(trimmed[4]) &&
            IsHexChar(trimmed[5]) &&
            IsHexChar(trimmed[6]))
        {
            return trimmed.ToUpperInvariant();
        }

        return resolvedFallback.ToUpperInvariant();
    }

    private static bool IsHexChar(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }

    private static string NormalizeImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var normalized = Path.GetFileName(fileName.Trim());
        var extension = Path.GetExtension(normalized).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".mp4" or ".webm" or ".ogg" or ".mov" or ".m4v" or ".mkv"
            ? normalized
            : string.Empty;
    }

    private static string NormalizeWallpaperBackgroundDisplayMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "Fill";
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "original" => "Original",
            "stretch" => "Stretch",
            _ => "Fill"
        };
    }

    private static double NormalizeFontSize(double value, double fallback)
    {
        var source = double.IsFinite(value) ? value : fallback;
        var clamped = Math.Clamp(source, 8d, 144d);
        return Math.Round(clamped, 1);
    }
}









using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Malie.Infrastructure;
using Malie.Interop;
using Malie.Models;
using Malie.Services;
using Malie.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Malie;

public partial class App : System.Windows.Application
{
    private const int MaxMeshyManagerLogLines = 2000;

    private static readonly JsonSerializerOptions SettingsSerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly HttpClient _geocodingHttp = new();
    private readonly HttpClient _currentLocationHttp = new();
    private readonly HttpClient _weatherHttp = new();
    private readonly HttpClient _landmarksHttp = new();
    private readonly HttpClient _poiImagesHttp = new();
    private readonly HttpClient _meshyHttp = new()
    {
        BaseAddress = new Uri("https://api.meshy.ai")
    };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _meshyQueueSync = new();
    private readonly DebugLogService _debugLog;
    private readonly object _refreshCtsSync = new();
    private readonly string _settingsFilePath;
    private readonly string _wallpaperAssetsRootPath;
    private readonly Queue<string> _meshyManagerLogs = new();
    private readonly List<string> _meshyDownloadQueue = new();
    private readonly HashSet<string> _meshyQueuedPoiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshyRowStatusOverride> _meshyStatusOverrides = new(StringComparer.OrdinalIgnoreCase);

    private GeocodingService? _geocodingService;
    private CurrentLocationService? _currentLocationService;
    private WeatherApiService? _weatherService;
    private PointOfInterestService? _pointOfInterestService;
    private PointOfInterestImageService? _pointOfInterestImageService;
    private MeshySceneService? _meshySceneService;
    private SceneDirectiveService? _sceneService;
    private WallpaperWindow? _wallpaperWindow;
    private SettingsWindow? _settingsWindow;
    private MeshyModelViewerWindow? _meshyModelViewerWindow;    private GlbOrientationWindow? _glbOrientationWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconAsset;
    private CancellationTokenSource? _updateLoopCts;
    private CancellationTokenSource? _activeRefreshCts;
    private PeriodicTimer? _periodicTimer;
    private CancellationTokenSource? _meshyQueueCts;
    private bool _isExiting;
    private bool _meshyQueueRunning;
    private bool _meshyManagerBusy;
    private string _locationInput = string.Empty;
    private string _meshyApiKey = string.Empty;
    private string _weatherApiKey = string.Empty;
    private string _latLngApiKey = string.Empty;
    private TemperatureScale _temperatureScale = TemperatureScale.Fahrenheit;
    private string _wallpaperBackgroundColor = "#7AA7D8";
    private string _wallpaperMonitorDeviceName = string.Empty;
    private string _wallpaperBackgroundImageFileName = string.Empty;
    private string _wallpaperBackgroundDisplayMode = "Fill";
    private bool _useAnimatedAiBackground;
    private bool _showWallpaperStatsOverlay = true;
    private WallpaperTextStyleSettings _wallpaperTextStyle = WallpaperTextStyleSettings.Default;
    private bool _showDebugLogPane = true;
    private LogFilterSettings _logFilters = LogFilterSettings.CreateDefault();
    private GlbOrientationSettings _glbOrientation = GlbOrientationSettings.Default;
    private MeshyViewerSettings _meshyViewerSettings = MeshyViewerSettings.Default;    private IReadOnlyList<string> _discoveredPointsOfInterest = Array.Empty<string>();
    private string _meshyManagerStatus = "Ready.";
    private string _meshyQueueStatus = "Queue idle.";
    private double _meshyQueueProgressPercent;
    private string _meshyQueueProgressText = "Queue idle.";
    private string? _activeMeshyQueuePoiKey;
    private SceneRenderPayload? _lastRenderedPayload;
    private DateTimeOffset _lastSessionRecoveryAtUtc = DateTimeOffset.MinValue;
    public App()
    {
        var appDataRoot = AppBranding.GetLocalAppDataRoot();
        var logsRoot = Path.Combine(appDataRoot, "logs");
        _settingsFilePath = Path.Combine(appDataRoot, "settings.json");
        _wallpaperAssetsRootPath = Path.Combine(appDataRoot, "cache", "wallpaper-assets");
        var logFilePath = Path.Combine(logsRoot, "app-debug.log");
        _debugLog = new DebugLogService(logFilePath);
        _debugLog.EntryAdded += OnDebugEntryAdded;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (HasSwitch(e.Args, "--cleanup-user-data"))
        {
            TryCleanupUserDataArtifacts();
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            LogDebug($"Startup on {Environment.OSVersion}; base directory: {AppContext.BaseDirectory}");
            LoadPersistedSettings();

            _geocodingService = new GeocodingService(_geocodingHttp);
            _currentLocationService = new CurrentLocationService(
                _currentLocationHttp,
                message => LogDebug(message, DebugLogCategory.LatLng));
            _weatherService = new WeatherApiService(
                _weatherHttp,
                message => LogDebug($"WeatherAPI: {message}", DebugLogCategory.Weather));
            _pointOfInterestService = new PointOfInterestService(
                _landmarksHttp,
                message => LogDebug(message, DebugLogCategory.LatLng));
            _pointOfInterestImageService = new PointOfInterestImageService(_poiImagesHttp);
            _meshySceneService = new MeshySceneService(_meshyHttp);
            _sceneService = new SceneDirectiveService();

            _settingsWindow = new SettingsWindow(
                _locationInput,
                _meshyApiKey,
                _weatherApiKey,
                _latLngApiKey,
                _temperatureScale,
                _wallpaperMonitorDeviceName,
                _wallpaperBackgroundColor,
                _wallpaperBackgroundImageFileName,
                _wallpaperBackgroundDisplayMode,
                _useAnimatedAiBackground,
                _showWallpaperStatsOverlay,
                _wallpaperTextStyle,
                _showDebugLogPane,
                _logFilters,
                _glbOrientation);
            _settingsWindow.SettingsSubmitted += OnSettingsSubmitted;
            _settingsWindow.UseCurrentLocationRequested += OnUseCurrentLocationRequested;
            _settingsWindow.LogFilterChanged += OnLogFilterChanged;
            _settingsWindow.DebugLogPaneVisibilityChanged += OnDebugLogPaneVisibilityChanged;
            _settingsWindow.MeshyModelViewerRequested += OnMeshyModelViewerRequested;
            _settingsWindow.OnboardingPoiRefreshRequested += OnOnboardingPoiRefreshRequested;
            _settingsWindow.MeshyManagerRefreshRequested += OnMeshyManagerRefreshRequested;
            _settingsWindow.MeshyManagerQueueRequested += OnMeshyManagerQueueRequested;
            _settingsWindow.MeshyManagerSetActiveRequested += OnMeshyManagerSetActiveRequested;
            _settingsWindow.MeshyManagerDeleteRequested += OnMeshyManagerDeleteRequested;
            _settingsWindow.MeshyManagerRenameRequested += OnMeshyManagerRenameRequested;
            _settingsWindow.MeshyManagerCustomPromptRequested += OnMeshyManagerCustomPromptRequested;
            _settingsWindow.MeshyManagerRotationRequested += OnMeshyManagerRotationRequested;
            _settingsWindow.MeshyManagerImportRequested += OnMeshyManagerImportRequested;
            _settingsWindow.MeshyManagerExportRequested += OnMeshyManagerExportRequested;            _settingsWindow.GlbOrientationChanged += OnLiveGlbOrientationChanged;
            _settingsWindow.WallpaperMonitorChanged += OnLiveWallpaperMonitorChanged;
            _settingsWindow.WallpaperBackgroundColorChanged += OnLiveWallpaperBackgroundColorChanged;
            _settingsWindow.WallpaperBackgroundImageChanged += OnLiveWallpaperBackgroundImageChanged;
            _settingsWindow.WallpaperBackgroundImageCleared += OnLiveWallpaperBackgroundImageCleared;
            _settingsWindow.WallpaperBackgroundDisplayModeChanged += OnLiveWallpaperBackgroundDisplayModeChanged;
            _settingsWindow.WallpaperAnimatedBackgroundChanged += OnLiveWallpaperAnimatedBackgroundChanged;
            _settingsWindow.WallpaperStatsOverlayChanged += OnLiveWallpaperStatsOverlayChanged;
            _settingsWindow.WallpaperTextStyleChanged += OnLiveWallpaperTextStyleChanged;
            _settingsWindow.RefreshRequested += OnRefreshRequested;
            _settingsWindow.ResetToOnboardingRequested += OnResetToOnboardingRequested;
            _settingsWindow.Closing += OnSettingsClosing;
            ApplyWindowIcon(_settingsWindow);
            _settingsWindow.SetDebugLines(GetVisibleLogLines(300));
            SyncMeshyManagerState();
            _settingsWindow.Show();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            ConfigureTrayIcon();

            EnsureOnboardingGuideVisible();
            if (IsOnboardingConfigurationComplete())
            {
                var started = await EnsureWallpaperStartedAsync("Startup");
                if (started)
                {
                    EnsurePeriodicRefreshLoopRunning();
                    _ = RefreshSceneAsync("Startup");
                }
            }
            else
            {
                _settingsWindow.SetStatus(
                    "Onboarding required before wallpaper starts: location, Meshy API key, Weather API key, LatLng API key, and scene configuration.");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Startup failed: {ex}");
            System.Windows.MessageBox.Show(
                $"Startup failed. See debug log for details.{Environment.NewLine}{ex.Message}",
                AppBranding.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        try
        {
            PersistSettings();
        }
        catch (Exception ex)
        {
            LogDebug($"Settings persist on exit failed: {ex.Message}");
        }

        _updateLoopCts?.Cancel();
        CancellationTokenSource? activeRefreshCts;
        lock (_refreshCtsSync)
        {
            activeRefreshCts = _activeRefreshCts;
            _activeRefreshCts = null;
        }
        activeRefreshCts?.Cancel();
        activeRefreshCts?.Dispose();
        CancellationTokenSource? meshyQueueCts;
        lock (_meshyQueueSync)
        {
            meshyQueueCts = _meshyQueueCts;
            _meshyQueueCts = null;
            _meshyQueueRunning = false;
            _meshyManagerBusy = false;
        }
        meshyQueueCts?.Cancel();
        meshyQueueCts?.Dispose();
        _periodicTimer?.Dispose();
        _refreshGate.Dispose();
        _trayIcon?.Dispose();
        _trayIconAsset?.Dispose();
        _glbOrientationWindow?.Close();
        _meshyModelViewerWindow?.Close();        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _geocodingHttp.Dispose();
        _currentLocationHttp.Dispose();
        _weatherHttp.Dispose();
        _landmarksHttp.Dispose();
        _poiImagesHttp.Dispose();
        _meshyHttp.Dispose();
        if (_wallpaperWindow is not null)
        {
            _wallpaperWindow.DebugMessage -= OnWallpaperDebugMessage;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.LogFilterChanged -= OnLogFilterChanged;
            _settingsWindow.UseCurrentLocationRequested -= OnUseCurrentLocationRequested;
            _settingsWindow.DebugLogPaneVisibilityChanged -= OnDebugLogPaneVisibilityChanged;
            _settingsWindow.MeshyModelViewerRequested -= OnMeshyModelViewerRequested;
            _settingsWindow.OnboardingPoiRefreshRequested -= OnOnboardingPoiRefreshRequested;
            _settingsWindow.MeshyManagerRefreshRequested -= OnMeshyManagerRefreshRequested;
            _settingsWindow.MeshyManagerQueueRequested -= OnMeshyManagerQueueRequested;
            _settingsWindow.MeshyManagerSetActiveRequested -= OnMeshyManagerSetActiveRequested;
            _settingsWindow.MeshyManagerDeleteRequested -= OnMeshyManagerDeleteRequested;
            _settingsWindow.MeshyManagerRenameRequested -= OnMeshyManagerRenameRequested;
            _settingsWindow.MeshyManagerCustomPromptRequested -= OnMeshyManagerCustomPromptRequested;
            _settingsWindow.MeshyManagerRotationRequested -= OnMeshyManagerRotationRequested;
            _settingsWindow.MeshyManagerImportRequested -= OnMeshyManagerImportRequested;
            _settingsWindow.MeshyManagerExportRequested -= OnMeshyManagerExportRequested;            _settingsWindow.GlbOrientationChanged -= OnLiveGlbOrientationChanged;
            _settingsWindow.WallpaperMonitorChanged -= OnLiveWallpaperMonitorChanged;
            _settingsWindow.WallpaperBackgroundColorChanged -= OnLiveWallpaperBackgroundColorChanged;
            _settingsWindow.WallpaperBackgroundImageChanged -= OnLiveWallpaperBackgroundImageChanged;
            _settingsWindow.WallpaperBackgroundImageCleared -= OnLiveWallpaperBackgroundImageCleared;
            _settingsWindow.WallpaperBackgroundDisplayModeChanged -= OnLiveWallpaperBackgroundDisplayModeChanged;
            _settingsWindow.WallpaperAnimatedBackgroundChanged -= OnLiveWallpaperAnimatedBackgroundChanged;
            _settingsWindow.WallpaperStatsOverlayChanged -= OnLiveWallpaperStatsOverlayChanged;
            _settingsWindow.WallpaperTextStyleChanged -= OnLiveWallpaperTextStyleChanged;
            _settingsWindow.ResetToOnboardingRequested -= OnResetToOnboardingRequested;
        }

        _debugLog.EntryAdded -= OnDebugEntryAdded;
        base.OnExit(e);
    }

    private void OnSettingsSubmitted(object? sender, LocationRenderSettings settings)
    {
        var priorLocationInput = _locationInput;
        var nextLocationInput = settings.LocationQuery.Trim();
        var locationChanged = !string.Equals(priorLocationInput, nextLocationInput, StringComparison.OrdinalIgnoreCase);

        _locationInput = nextLocationInput;
        _meshyApiKey = settings.MeshyApiKey.Trim();
        _weatherApiKey = settings.WeatherApiKey.Trim();
        _latLngApiKey = settings.LatLngApiKey.Trim();        _temperatureScale = settings.TemperatureScale;
        _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(settings.WallpaperMonitorDeviceName);
        _wallpaperBackgroundColor = NormalizeHexColor(settings.WallpaperBackgroundColor, _wallpaperBackgroundColor);
        _wallpaperBackgroundImageFileName = NormalizeWallpaperBackgroundImageFileName(settings.WallpaperBackgroundImageFileName);
        _wallpaperBackgroundDisplayMode = NormalizeWallpaperBackgroundDisplayMode(settings.WallpaperBackgroundDisplayMode);
        _useAnimatedAiBackground = settings.UseAnimatedAiBackground;
        _showWallpaperStatsOverlay = settings.ShowWallpaperStatsOverlay;
        _wallpaperTextStyle = (settings.WallpaperTextStyle ?? WallpaperTextStyleSettings.Default).Normalize();
        _showDebugLogPane = settings.ShowDebugLogPane;
        _logFilters = settings.LogFilters;
        _glbOrientation = settings.GlbOrientation.Normalize();
        _settingsWindow?.SetGlbOrientation(_glbOrientation);
        _settingsWindow?.SetWallpaperMonitorDeviceName(_wallpaperMonitorDeviceName);
        _settingsWindow?.SetWallpaperBackgroundColor(_wallpaperBackgroundColor);
        _settingsWindow?.SetWallpaperBackgroundImageFileName(_wallpaperBackgroundImageFileName);
        _settingsWindow?.SetWallpaperBackgroundDisplayMode(_wallpaperBackgroundDisplayMode);
        _settingsWindow?.SetWallpaperAnimatedBackgroundEnabled(_useAnimatedAiBackground);
        _settingsWindow?.SetWallpaperStatsOverlayVisible(_showWallpaperStatsOverlay);
        _settingsWindow?.SetWallpaperTextStyle(_wallpaperTextStyle);
        _settingsWindow?.SetDebugLogPaneVisible(_showDebugLogPane);        _ = ApplyGlbOrientationAsync("Settings submitted");
        _ = ApplyTemperatureUnitAsync("Settings submitted");
        _ = ApplyWallpaperMonitorAsync("Settings submitted");
        _ = ApplyWallpaperBackgroundColorAsync("Settings submitted");
        _ = ApplyWallpaperBackgroundImageAsync("Settings submitted");
        _ = ApplyWallpaperBackgroundDisplayModeAsync("Settings submitted");
        _ = ApplyWallpaperAnimatedBackgroundAsync("Settings submitted");
        _ = ApplyWallpaperStatsOverlayAsync("Settings submitted");
        _ = ApplyWallpaperTextStyleAsync("Settings submitted");
        RefreshDebugLogView();
        var apiKeyState = string.IsNullOrWhiteSpace(_meshyApiKey) ? "empty" : "set";
        var weatherApiKeyState = string.IsNullOrWhiteSpace(_weatherApiKey) ? "empty" : "set";
        var temperatureScaleState = _temperatureScale == TemperatureScale.Celsius ? "Celsius" : "Fahrenheit";
        var wallpaperMonitorState = string.IsNullOrWhiteSpace(_wallpaperMonitorDeviceName)
            ? "primary"
            : _wallpaperMonitorDeviceName;
        var latLngKeyState = string.IsNullOrWhiteSpace(_latLngApiKey) ? "empty" : "set";
        var wallpaperColorState = _wallpaperBackgroundColor;
        var wallpaperImageState = string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName)
            ? "none"
            : _wallpaperBackgroundImageFileName;
        var wallpaperDisplayModeState = _wallpaperBackgroundDisplayMode;
        var animatedBackgroundState = _useAnimatedAiBackground ? "enabled" : "disabled";
        var wallpaperStatsOverlayState = _showWallpaperStatsOverlay ? "shown" : "hidden";
        var wallpaperFontState = $"time={_wallpaperTextStyle.TimeFontFamily}, location={_wallpaperTextStyle.LocationFontFamily}";
        PersistSettings();
        LogDebug(
            $"Settings submitted: location '{_locationInput}', Meshy API key {apiKeyState}. " +
            $"Weather API key is {weatherApiKeyState}. " +
            $"Temperature scale is {temperatureScaleState}. " +
            $"Wallpaper monitor is {wallpaperMonitorState}. " +
            $"LatLng API key is {latLngKeyState}. " +
            $"Wallpaper background is {wallpaperColorState}. " +
            $"Wallpaper background image is {wallpaperImageState}. " +
            $"Wallpaper background display mode is {wallpaperDisplayModeState}. " +
            $"Animated wallpaper background is {animatedBackgroundState}. " +
            $"Wallpaper stats overlay is {wallpaperStatsOverlayState}. " +
            $"Wallpaper font is {wallpaperFontState}. " +
            $"Scene profile active.");

        if (locationChanged)
        {
            CancellationTokenSource? queueCtsToDispose = null;
            lock (_meshyQueueSync)
            {
                queueCtsToDispose = _meshyQueueCts;
                _meshyQueueCts = null;
                _meshyQueueRunning = false;
                _meshyManagerBusy = false;
                _meshyDownloadQueue.Clear();
                _meshyQueuedPoiKeys.Clear();
                _meshyStatusOverrides.Clear();
                _activeMeshyQueuePoiKey = null;
                _meshyQueueStatus = "Queue idle.";
                _meshyQueueProgressPercent = 0;
                _meshyQueueProgressText = _meshyQueueStatus;
            }

            _meshyViewerSettings = _meshyViewerSettings with { ActiveModelRelativePath = string.Empty };
            _meshyModelViewerWindow?.SetViewerSettings(_meshyViewerSettings);
            PersistSettings();
            LogDebug("Location changed. Cleared active cached model selection to avoid stale city defaults.", DebugLogCategory.Meshy);

            queueCtsToDispose?.Cancel();
            queueCtsToDispose?.Dispose();

            _discoveredPointsOfInterest = Array.Empty<string>();
            _settingsWindow?.SetOnboardingPois(Array.Empty<string>());
            AppendMeshyManagerLog("Meshy queue cleared because location changed.");
            SyncMeshyViewerContext();
            SyncMeshyManagerState();
        }
        else
        {
            // Keep queue execution and pending rows intact when only non-location settings change.
            SyncMeshyManagerState(_discoveredPointsOfInterest);
        }

        EnsureOnboardingGuideVisible();
        if (IsOnboardingConfigurationComplete())
        {
            _ = EnsureWallpaperStartedAndRefreshAsync("Settings submitted");
        }
    }

    private async void OnUseCurrentLocationRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null || _currentLocationService is null)
        {
            return;
        }

        try
        {
            _settingsWindow.SetStatus("Detecting current location...");
            var detectedLocation = await _currentLocationService.ResolveLocationQueryAsync(CancellationToken.None);
            _settingsWindow.SetLocation(detectedLocation);
            _settingsWindow.SetStatus($"Detected current location: {detectedLocation}. Click Apply Settings to save.");
            LogDebug($"Current location detected and populated: '{detectedLocation}'.", DebugLogCategory.LatLng);
        }
        catch (Exception ex)
        {
            _settingsWindow.SetStatus($"Current location detection failed: {ex.Message}");
            LogDebug($"Current location detection failed: {ex.Message}", DebugLogCategory.Error);
        }
    }

    private void OnLogFilterChanged(object? sender, LogFilterSettings filters)
    {
        _logFilters = filters;
        PersistSettings();
        RefreshDebugLogView();
    }

    private void OnDebugLogPaneVisibilityChanged(object? sender, bool isVisible)
    {
        if (_showDebugLogPane == isVisible)
        {
            return;
        }

        _showDebugLogPane = isVisible;
        PersistSettings();
        LogDebug($"Debug log pane visibility changed: {(isVisible ? "shown" : "hidden")}.");
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        LogDebug("Refresh requested from settings.");
        if (!IsOnboardingConfigurationComplete())
        {
            _settingsWindow?.SetStatus(
                "Onboarding incomplete. Configure location and API keys before refresh.");
            EnsureOnboardingGuideVisible();
            return;
        }

        var started = await EnsureWallpaperStartedAsync("Settings refresh");
        if (!started)
        {
            return;
        }

        EnsurePeriodicRefreshLoopRunning();
        _ = RefreshSceneAsync("Settings refresh");
    }

    private async void OnOnboardingPoiRefreshRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null || _geocodingService is null || _pointOfInterestService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_locationInput))
        {
            _settingsWindow.SetStatus("Set a location before refreshing POIs.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_latLngApiKey))
        {
            _settingsWindow.SetStatus("Set a LatLng API key before refreshing POIs.");
            return;
        }

        try
        {
            _settingsWindow.SetStatus("Refreshing POIs from LatLng...");
            var coordinates = await _geocodingService.ResolveAsync(_locationInput, CancellationToken.None);
            var cachedPoiMeshes = _meshySceneService?.GetCachedMeshes() ?? Array.Empty<PointOfInterestMesh>();
            var cachedPoiNames = cachedPoiMeshes
                .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
                .Select(mesh => mesh.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var discoveredPointsOfInterest = await _pointOfInterestService.GetNearbyLandmarksAsync(
                coordinates,
                _locationInput,
                _latLngApiKey,
                cachedPoiNames,
                48,
                CancellationToken.None);
            discoveredPointsOfInterest =
                _meshySceneService?.ApplyPoiNameOverrides(discoveredPointsOfInterest) ?? discoveredPointsOfInterest;
            _discoveredPointsOfInterest = discoveredPointsOfInterest;
            _settingsWindow.SetOnboardingPois(_discoveredPointsOfInterest);
            SyncMeshyManagerState(_discoveredPointsOfInterest);
            _settingsWindow.SetStatus(
                _discoveredPointsOfInterest.Count == 0
                    ? "No POIs returned from LatLng for the current location."
                    : $"Loaded {_discoveredPointsOfInterest.Count} POI(s) from LatLng.");
            LogDebug(
                _discoveredPointsOfInterest.Count == 0
                    ? "Onboarding POI refresh returned no results."
                    : $"Onboarding POI refresh: {string.Join(" | ", _discoveredPointsOfInterest)}",
                DebugLogCategory.LatLng);
        }
        catch (Exception ex)
        {
            _settingsWindow.SetStatus($"POI refresh failed: {ex.Message}");
            LogDebug($"Onboarding POI refresh failed: {ex.Message}", DebugLogCategory.Error);
        }
    }

    private void OnResetToOnboardingRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        try
        {
            _settingsWindow.SetStatus("Resetting application data...");
            _updateLoopCts?.Cancel();
            lock (_refreshCtsSync)
            {
                _activeRefreshCts?.Cancel();
            }

            _meshySceneService?.ClearCache();

            var appDataRoot = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(appDataRoot))
            {
                TryDeleteFile(_settingsFilePath);
                TryDeleteDirectory(Path.Combine(appDataRoot, "cache"));
                TryDeleteDirectory(Path.Combine(appDataRoot, "logs"));
            }

            _locationInput = string.Empty;
            _meshyApiKey = string.Empty;
            _weatherApiKey = string.Empty;
            _latLngApiKey = string.Empty;
            _temperatureScale = TemperatureScale.Fahrenheit;
            _wallpaperMonitorDeviceName = string.Empty;
            _wallpaperBackgroundColor = "#7AA7D8";
            _wallpaperBackgroundImageFileName = string.Empty;
            _wallpaperBackgroundDisplayMode = "Fill";
            _useAnimatedAiBackground = false;
            _showWallpaperStatsOverlay = true;
            _wallpaperTextStyle = WallpaperTextStyleSettings.Default;
            _showDebugLogPane = true;
            _logFilters = LogFilterSettings.CreateDefault();
            _glbOrientation = GlbOrientationSettings.Default;
            _meshyViewerSettings = MeshyViewerSettings.Default;            _discoveredPointsOfInterest = Array.Empty<string>();
            lock (_meshyQueueSync)
            {
                _meshyDownloadQueue.Clear();
                _meshyQueuedPoiKeys.Clear();
                _meshyStatusOverrides.Clear();
                _activeMeshyQueuePoiKey = null;
            }
            _meshyManagerBusy = false;
            _meshyQueueRunning = false;
            _meshyManagerStatus = "Ready.";
            _meshyQueueStatus = "Queue idle.";
            _meshyQueueProgressPercent = 0;
            _meshyQueueProgressText = _meshyQueueStatus;
            _meshyManagerLogs.Clear();
            _lastRenderedPayload = null;

            PersistSettings();
            LogDebug("Application reset completed. Exiting for clean restart.");
            ExitApplication();
        }
        catch (Exception ex)
        {
            _settingsWindow.SetStatus($"Reset failed: {ex.Message}");
            LogDebug($"Application reset failed: {ex}");
        }
    }

    private void OnGlbOrientationViewRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_glbOrientationWindow is null || !_glbOrientationWindow.IsLoaded)
            {
                _glbOrientationWindow = new GlbOrientationWindow(_glbOrientation);
                _glbOrientationWindow.OrientationApplied += OnGlbOrientationWindowApplied;
                ApplyWindowIcon(_glbOrientationWindow);
                _glbOrientationWindow.Closed += (_, _) =>
                {
                    if (_glbOrientationWindow is not null)
                    {
                        _glbOrientationWindow.OrientationApplied -= OnGlbOrientationWindowApplied;
                    }

                    _glbOrientationWindow = null;
                };
                _glbOrientationWindow.Show();
                return;
            }

            _glbOrientationWindow.SetOrientation(_glbOrientation);
            _glbOrientationWindow.Show();
            _glbOrientationWindow.WindowState = WindowState.Normal;
            _glbOrientationWindow.Activate();
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to open GLB orientation view: {ex}");
            _settingsWindow?.SetStatus($"Failed to open orientation view: {ex.Message}");
        }
    }

    private void OnGlbOrientationWindowApplied(object? sender, GlbOrientationSettings orientation)
    {
        _glbOrientation = orientation.Normalize();
        _settingsWindow?.SetGlbOrientation(_glbOrientation);
        PersistSettings();
        _ = ApplyGlbOrientationAsync("Orientation view apply");
    }

    private void OnLiveGlbOrientationChanged(object? sender, GlbOrientationSettings orientation)
    {
        _glbOrientation = orientation.Normalize();
        PersistSettings();
        _ = ApplyGlbOrientationAsync("Live orientation update");
    }

    private void OnLiveWallpaperMonitorChanged(object? sender, string monitorDeviceName)
    {
        var normalized = NormalizeWallpaperMonitorDeviceName(monitorDeviceName);
        if (string.Equals(normalized, _wallpaperMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _wallpaperMonitorDeviceName = normalized;
        PersistSettings();
        _settingsWindow?.SetWallpaperMonitorDeviceName(_wallpaperMonitorDeviceName);
        LogDebug(
            string.IsNullOrWhiteSpace(_wallpaperMonitorDeviceName)
                ? "Wallpaper monitor updated live: primary."
                : $"Wallpaper monitor updated live: {_wallpaperMonitorDeviceName}.");
        _ = ApplyWallpaperMonitorAsync("Live monitor update");
    }

    private void OnLiveWallpaperBackgroundColorChanged(object? sender, string color)
    {
        var normalized = NormalizeHexColor(color, _wallpaperBackgroundColor);
        if (string.Equals(normalized, _wallpaperBackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _wallpaperBackgroundColor = normalized;
        PersistSettings();
        LogDebug($"Wallpaper background color updated live: {_wallpaperBackgroundColor}.");
        _ = ApplyWallpaperBackgroundColorAsync("Live background update");
    }

    private void OnLiveWallpaperBackgroundImageChanged(object? sender, WallpaperBackgroundImageChangeRequest request)
    {
        try
        {
            var savedFileName = SaveWallpaperBackgroundImage(request);
            _wallpaperBackgroundImageFileName = savedFileName;
            PersistSettings();
            _settingsWindow?.SetWallpaperBackgroundImageFileName(_wallpaperBackgroundImageFileName);
            LogDebug($"Wallpaper background media updated live: {_wallpaperBackgroundImageFileName}.");
            _ = ApplyWallpaperBackgroundImageAsync("Live background media update");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper background media update failed: {ex.Message}", DebugLogCategory.Error);
            _settingsWindow?.SetStatus($"Background media update failed: {ex.Message}");
        }
    }

    private void OnLiveWallpaperBackgroundImageCleared(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName))
        {
            TryDeleteFile(Path.Combine(_wallpaperAssetsRootPath, _wallpaperBackgroundImageFileName));
        }

        if (string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName))
        {
            _ = ApplyWallpaperBackgroundImageAsync("Live background media clear");
            return;
        }

        _wallpaperBackgroundImageFileName = string.Empty;
        PersistSettings();
        _settingsWindow?.SetWallpaperBackgroundImageFileName(string.Empty);
        LogDebug("Wallpaper background media cleared. Falling back to color.");
        _ = ApplyWallpaperBackgroundImageAsync("Live background media clear");
    }

    private void OnLiveWallpaperBackgroundDisplayModeChanged(object? sender, string mode)
    {
        var normalized = NormalizeWallpaperBackgroundDisplayMode(mode);
        if (string.Equals(normalized, _wallpaperBackgroundDisplayMode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _wallpaperBackgroundDisplayMode = normalized;
        PersistSettings();
        LogDebug($"Wallpaper background display mode updated live: {_wallpaperBackgroundDisplayMode}.");
        _ = ApplyWallpaperBackgroundDisplayModeAsync("Live background display mode update");
    }

    private void OnLiveWallpaperAnimatedBackgroundChanged(object? sender, bool enabled)
    {
        if (_useAnimatedAiBackground == enabled)
        {
            return;
        }

        _useAnimatedAiBackground = enabled;
        PersistSettings();
        LogDebug($"Animated wallpaper background updated live: {(_useAnimatedAiBackground ? "enabled" : "disabled")}.");
        _ = ApplyWallpaperAnimatedBackgroundAsync("Live animated background update");
    }

    private void OnLiveWallpaperStatsOverlayChanged(object? sender, bool visible)
    {
        if (_showWallpaperStatsOverlay == visible)
        {
            return;
        }

        _showWallpaperStatsOverlay = visible;
        PersistSettings();
        LogDebug(
            $"Wallpaper stats overlay updated live: {(_showWallpaperStatsOverlay ? "shown" : "hidden")}.");
        _ = ApplyWallpaperStatsOverlayAsync("Live stats overlay update");
    }

    private void OnLiveWallpaperTextStyleChanged(object? sender, WallpaperTextStyleSettings textStyle)
    {
        var normalized = (textStyle ?? WallpaperTextStyleSettings.Default).Normalize();
        if (normalized == _wallpaperTextStyle)
        {
            return;
        }

        _wallpaperTextStyle = normalized;
        PersistSettings();
        LogDebug(
            $"Wallpaper text style updated live: timeFont='{_wallpaperTextStyle.TimeFontFamily}', locationFont='{_wallpaperTextStyle.LocationFontFamily}', " +
            $"time={_wallpaperTextStyle.TimeFontSize:0.#}, location={_wallpaperTextStyle.LocationFontSize:0.#}, " +
            $"date={_wallpaperTextStyle.DateFontSize:0.#}, temp={_wallpaperTextStyle.TemperatureFontSize:0.#}.");
        _ = ApplyWallpaperTextStyleAsync("Live text style update");
    }

    private void OnMeshyCacheClearRequested(object? sender, EventArgs e)
    {
        if (_meshySceneService is null || _settingsWindow is null)
        {
            return;
        }

        try
        {
            _meshySceneService.ClearCache();
            _settingsWindow.SetStatus("Meshy cache cleared.");
            LogDebug("Meshy cache cleared from settings.");
            SyncMeshyViewerContext();
        }
        catch (Exception ex)
        {
            _settingsWindow.SetStatus($"Failed to clear Meshy cache: {ex.Message}");
            LogDebug($"Meshy cache clear failed: {ex}");
        }
    }

    private void OnMeshyManagerRefreshRequested(object? sender, EventArgs e)
    {
        SyncMeshyManagerState();
    }

    private void OnMeshyManagerQueueRequested(object? sender, MeshyQueueRequest request)
    {
        if (_meshySceneService is null || string.IsNullOrWhiteSpace(request.PoiName))
        {
            return;
        }

        var poiName = _meshySceneService.ResolvePoiName(request.PoiName);
        var poiKey = NormalizePoiKey(poiName);
        if (string.IsNullOrWhiteSpace(poiKey))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_meshyApiKey))
        {
            _meshyManagerStatus = "Meshy API key is required before queueing POIs.";
            lock (_meshyQueueSync)
            {
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Meshy API key missing.", "error");
            }
            AppendMeshyManagerLog($"Queue rejected for '{poiName}': Meshy API key is missing.");
            SyncMeshyManagerState();
            return;
        }

        if (HasCachedMeshForPoi(poiKey))
        {
            _meshyManagerStatus = $"POI '{poiName}' is already cached.";
            lock (_meshyQueueSync)
            {
                _meshyStatusOverrides.Remove(poiKey);
            }
            SyncMeshyManagerState();
            return;
        }

        var alreadyQueued = false;
        lock (_meshyQueueSync)
        {
            if (_meshyQueuedPoiKeys.Contains(poiKey))
            {
                alreadyQueued = true;
            }
            else
            {
                if (request.Prioritize)
                {
                    _meshyDownloadQueue.Insert(0, poiName);
                }
                else
                {
                    _meshyDownloadQueue.Add(poiName);
                }

                _meshyQueuedPoiKeys.Add(poiKey);
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride(
                    request.Prioritize ? "Queued (priority)." : "Queued.",
                    "queued");
                _meshyQueueStatus = $"Queue: {_meshyDownloadQueue.Count} pending.";
                if (!_meshyQueueRunning)
                {
                    _meshyQueueProgressPercent = 5;
                    _meshyQueueProgressText = request.Prioritize
                        ? $"Priority queue added for '{poiName}'."
                        : $"Queued '{poiName}' for Meshy generation.";
                }
            }
        }

        if (alreadyQueued)
        {
            _meshyManagerStatus = $"POI '{poiName}' is already queued.";
            SyncMeshyManagerState();
            return;
        }

        _meshyManagerStatus = request.Prioritize
            ? $"Priority queue added for '{poiName}'."
            : $"Queued '{poiName}' for Meshy generation.";
        AppendMeshyManagerLog(_meshyManagerStatus);
        SyncMeshyManagerState();
        _ = StartMeshyQueueProcessorIfNeededAsync();
    }

    private void OnMeshyManagerSetActiveRequested(object? sender, string localRelativePath)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var normalizedPath = NormalizeModelRelativePath(localRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            _meshyManagerStatus = "Active model path is invalid.";
            SyncMeshyManagerState();
            return;
        }

        var cachedMesh = _meshySceneService.GetCachedMeshes().FirstOrDefault(mesh =>
            string.Equals(
                NormalizeModelRelativePath(mesh.LocalRelativePath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
        if (cachedMesh is null)
        {
            _meshyManagerStatus = "Unable to set active model: cached model was not found.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        _meshyViewerSettings = _meshyViewerSettings with { ActiveModelRelativePath = cachedMesh.LocalRelativePath };
        PersistSettings();
        _meshyModelViewerWindow?.SetViewerSettings(_meshyViewerSettings);

        _meshyManagerStatus =
            $"Active wallpaper model set to '{cachedMesh.Name}' ({Path.GetFileName(cachedMesh.LocalRelativePath)}).";
        AppendMeshyManagerLog(_meshyManagerStatus);
        SyncMeshyManagerState();
        _ = ApplyMeshyViewerSettingsAsync("Active model selected in Meshy manager");
    }

    private void OnMeshyManagerDeleteRequested(object? sender, string localRelativePath)
    {
        if (_meshySceneService is null || string.IsNullOrWhiteSpace(localRelativePath))
        {
            return;
        }

        if (!_meshySceneService.TryDeleteCachedModel(localRelativePath, out var message))
        {
            _meshyManagerStatus = message;
            AppendMeshyManagerLog(message);
            SyncMeshyManagerState();
            return;
        }

        if (string.Equals(_meshyViewerSettings.ActiveModelRelativePath, localRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _meshyViewerSettings = _meshyViewerSettings with { ActiveModelRelativePath = string.Empty };
            PersistSettings();
            _ = ApplyMeshyViewerSettingsAsync("Meshy model deleted");
        }

        _meshyManagerStatus = message;
        AppendMeshyManagerLog(message);
        SyncMeshyViewerContext();
    }

    private void OnMeshyManagerRenameRequested(object? sender, MeshyRenameRequest request)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var oldName = request.OldName?.Trim() ?? string.Empty;
        var newName = request.NewName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var oldKey = NormalizePoiKey(oldName);
        var newKey = NormalizePoiKey(newName);
        if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase) &&
            IsPoiNameInUse(newKey))
        {
            _meshyManagerStatus = $"Rename failed: POI name '{newName}' already exists.";
            lock (_meshyQueueSync)
            {
                _meshyStatusOverrides[newKey] = new MeshyRowStatusOverride(_meshyManagerStatus, "error");
            }
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.LocalRelativePath))
        {
            if (!_meshySceneService.TryRenameCachedTargetByModelPath(request.LocalRelativePath, newName, out var renameMessage))
            {
                _meshyManagerStatus = renameMessage;
                lock (_meshyQueueSync)
                {
                    _meshyStatusOverrides[newKey] = new MeshyRowStatusOverride(renameMessage, "error");
                }
                AppendMeshyManagerLog(renameMessage);
                SyncMeshyManagerState();
                return;
            }
        }

        _meshySceneService.SetPoiNameOverride(oldName, newName);
        _discoveredPointsOfInterest = _discoveredPointsOfInterest
            .Select(poi => string.Equals(poi.Trim(), oldName, StringComparison.OrdinalIgnoreCase) ? newName : poi)
            .Where(poi => !string.IsNullOrWhiteSpace(poi))
            .Select(poi => poi.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _settingsWindow?.SetOnboardingPois(_discoveredPointsOfInterest);

        lock (_meshyQueueSync)
        {
            for (var index = 0; index < _meshyDownloadQueue.Count; index += 1)
            {
                if (NormalizePoiKey(_meshyDownloadQueue[index]) == oldKey)
                {
                    _meshyDownloadQueue[index] = newName;
                }
            }

            _meshyQueuedPoiKeys.Remove(oldKey);
            if (!_meshyDownloadQueue.All(item => NormalizePoiKey(item) != newKey))
            {
                _meshyQueuedPoiKeys.Add(newKey);
            }
        }

        lock (_meshyQueueSync)
        {
            if (_meshyStatusOverrides.Remove(oldKey, out var priorStatus))
            {
                _meshyStatusOverrides[newKey] = priorStatus;
            }
        }

        _meshyManagerStatus = $"Renamed POI '{oldName}' to '{newName}'.";
        AppendMeshyManagerLog(_meshyManagerStatus);
        SyncMeshyViewerContext(_discoveredPointsOfInterest);
    }

    private async void OnMeshyManagerCustomPromptRequested(object? sender, string prompt)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var normalizedPrompt = prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_meshyApiKey))
        {
            _meshyManagerStatus = "Meshy API key is required before submitting a custom prompt.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        var queueRunning = false;
        lock (_meshyQueueSync)
        {
            queueRunning = _meshyQueueRunning;
        }

        if (queueRunning)
        {
            _meshyManagerStatus = "Wait for the Meshy queue to finish before submitting a custom prompt.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        _meshyManagerBusy = true;
        _meshyManagerStatus = "Submitting custom Meshy prompt...";
        AppendMeshyManagerLog($"Custom prompt submitted ({normalizedPrompt.Length} chars).");
        SyncMeshyManagerState();

        try
        {
            var result = await _meshySceneService.GenerateFromCustomPromptAsync(
                normalizedPrompt,
                _meshyApiKey,
                CancellationToken.None,
                message =>
                {
                    AppendMeshyManagerLog(message);
                    _meshyManagerStatus = message;
                    SyncMeshyManagerState();
                });

            if (result.UsedMeshy && result.GeneratedMeshes.Count > 0)
            {
                var updatedPois = _discoveredPointsOfInterest
                    .Concat(result.GeneratedMeshes.Select(mesh => mesh.Name))
                    .Where(poi => !string.IsNullOrWhiteSpace(poi))
                    .Select(poi => poi.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _discoveredPointsOfInterest = _meshySceneService.ApplyPoiNameOverrides(updatedPois);
                _settingsWindow?.SetOnboardingPois(_discoveredPointsOfInterest);
                _meshyManagerStatus = result.Message;
                AppendMeshyManagerLog(
                    $"Custom prompt completed. Generated {result.GeneratedMeshes.Count} cached mesh model(s).");
            }
            else
            {
                _meshyManagerStatus = $"Failed: {result.Message}";
                AppendMeshyManagerLog(_meshyManagerStatus);
            }
        }
        catch (OperationCanceledException)
        {
            _meshyManagerStatus = "Custom prompt canceled.";
            AppendMeshyManagerLog("Custom prompt canceled.");
        }
        catch (Exception ex)
        {
            _meshyManagerStatus = $"Custom prompt failed: {ex.Message}";
            AppendMeshyManagerLog(_meshyManagerStatus);
            LogDebug($"Meshy custom prompt failed: {ex.Message}", DebugLogCategory.Error);
        }
        finally
        {
            lock (_meshyQueueSync)
            {
                _meshyManagerBusy = _meshyQueueRunning;
            }

            SyncMeshyViewerContext(_discoveredPointsOfInterest);
        }
    }

    private void OnMeshyManagerRotationRequested(object? sender, double minutes)
    {
        var normalizedMinutes = Math.Max(0, Math.Min(1440, double.IsFinite(minutes) ? minutes : 0));
        _meshyViewerSettings = _meshyViewerSettings with { RotationMinutes = normalizedMinutes };
        PersistSettings();
        _meshyModelViewerWindow?.SetViewerSettings(_meshyViewerSettings);
        _meshyManagerStatus = normalizedMinutes <= 0
            ? "Model rotation disabled."
            : $"Model rotation set to every {normalizedMinutes:0.###} minute(s).";
        AppendMeshyManagerLog(_meshyManagerStatus);
        SyncMeshyManagerState();
        _ = ApplyMeshyViewerSettingsAsync("Meshy rotation changed from settings");
    }

    private void OnMeshyManagerExportRequested(object? sender, MeshyExportRequest request)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var localRelativePath = NormalizeModelRelativePath(request.LocalRelativePath);
        if (string.IsNullOrWhiteSpace(localRelativePath))
        {
            _meshyManagerStatus = "Export failed: invalid model path.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        if (!_meshySceneService.TryResolveCachedModelFilePath(localRelativePath, out var sourcePath, out var resolveMessage))
        {
            _meshyManagerStatus = resolveMessage;
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        try
        {
            var sourceFileName = Path.GetFileName(sourcePath);
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Meshy GLB",
                Filter = "GLB model (*.glb)|*.glb|All files (*.*)|*.*",
                DefaultExt = ".glb",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = sourceFileName
            };

            var owner = _settingsWindow;
            var accepted = owner is null ? saveDialog.ShowDialog() : saveDialog.ShowDialog(owner);
            if (accepted != true || string.IsNullOrWhiteSpace(saveDialog.FileName))
            {
                _meshyManagerStatus = "Meshy export canceled.";
                AppendMeshyManagerLog(_meshyManagerStatus);
                SyncMeshyManagerState();
                return;
            }

            var targetPath = saveDialog.FileName;
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }
            File.Copy(sourcePath, targetPath, overwrite: true);

            _meshyManagerStatus = $"Exported '{sourceFileName}' to '{targetPath}'.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
        }
        catch (Exception ex)
        {
            _meshyManagerStatus = $"Export failed: {ex.Message}";
            AppendMeshyManagerLog(_meshyManagerStatus);
            LogDebug($"Meshy export failed: {ex}", DebugLogCategory.Error);
            SyncMeshyManagerState();
        }
    }

    private void OnMeshyManagerImportRequested(object? sender, MeshyImportRequest request)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var poiName = request.PoiName?.Trim() ?? string.Empty;
        var fileName = Path.GetFileName(request.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(poiName) || string.IsNullOrWhiteSpace(fileName))
        {
            _meshyManagerStatus = "Import failed: POI name and file name are required.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        if (!fileName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            _meshyManagerStatus = "Import failed: only .glb files are supported.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        var poiKey = NormalizePoiKey(poiName);
        if (string.IsNullOrWhiteSpace(poiKey))
        {
            _meshyManagerStatus = "Import failed: POI name is invalid.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        if (IsPoiNameInUse(poiKey))
        {
            _meshyManagerStatus = $"Import failed: POI name '{poiName}' already exists.";
            lock (_meshyQueueSync)
            {
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride(_meshyManagerStatus, "error");
            }
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        if (!TryDecodeMeshyImportDataUrl(request.DataUrl, out _, out var bytes))
        {
            _meshyManagerStatus = "Import failed: GLB payload is invalid.";
            AppendMeshyManagerLog(_meshyManagerStatus);
            SyncMeshyManagerState();
            return;
        }

        var importFolder = Path.Combine(
            Path.GetTempPath(),
            AppBranding.SafeName,
            "meshy-imports");
        var tempFilePath = Path.Combine(importFolder, $"{Guid.NewGuid():N}.glb");
        Directory.CreateDirectory(importFolder);

        try
        {
            File.WriteAllBytes(tempFilePath, bytes);
            if (!_meshySceneService.TryImportGlbModel(tempFilePath, poiName, out var importedMesh, out var message))
            {
                _meshyManagerStatus = message;
                lock (_meshyQueueSync)
                {
                    _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride(message, "error");
                }
                AppendMeshyManagerLog(message);
                SyncMeshyManagerState();
                return;
            }

            lock (_meshyQueueSync)
            {
                _meshyDownloadQueue.RemoveAll(item =>
                    string.Equals(NormalizePoiKey(item), poiKey, StringComparison.OrdinalIgnoreCase));
                _meshyQueuedPoiKeys.Remove(poiKey);
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Cached.", "cached");
                _meshyQueueStatus = _meshyDownloadQueue.Count == 0
                    ? "Queue idle."
                    : $"Queue: {_meshyDownloadQueue.Count} pending.";
            }

            if (_discoveredPointsOfInterest.All(item =>
                    !string.Equals(NormalizePoiKey(item), poiKey, StringComparison.OrdinalIgnoreCase)))
            {
                _discoveredPointsOfInterest = _discoveredPointsOfInterest
                    .Append(poiName)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _settingsWindow?.SetOnboardingPois(_discoveredPointsOfInterest);
            }

            _meshyManagerStatus = message;
            AppendMeshyManagerLog(message);

            if (importedMesh is not null)
            {
                LogDebug(
                    $"Imported GLB '{fileName}' as POI '{importedMesh.Name}' ({importedMesh.LocalRelativePath}).",
                    DebugLogCategory.Meshy);
            }

            SyncMeshyViewerContext(_discoveredPointsOfInterest);
        }
        catch (Exception ex)
        {
            _meshyManagerStatus = $"Import failed: {ex.Message}";
            AppendMeshyManagerLog(_meshyManagerStatus);
            LogDebug($"Meshy GLB import failed: {ex}", DebugLogCategory.Error);
            SyncMeshyManagerState();
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }

    private async Task StartMeshyQueueProcessorIfNeededAsync()
    {
        if (_meshySceneService is null)
        {
            return;
        }

        CancellationToken token = CancellationToken.None;
        lock (_meshyQueueSync)
        {
            if (_meshyQueueRunning)
            {
                return;
            }

            if (_meshyDownloadQueue.Count == 0)
            {
                _meshyQueueStatus = "Queue idle.";
                _meshyManagerBusy = false;
                _meshyQueueProgressPercent = 0;
                _meshyQueueProgressText = "Queue idle.";
            }
            else
            {
                _meshyQueueRunning = true;
                _meshyManagerBusy = true;
                _meshyQueueCts?.Cancel();
                _meshyQueueCts?.Dispose();
                _meshyQueueCts = new CancellationTokenSource();
                token = _meshyQueueCts.Token;
            }
        }

        if (!_meshyQueueRunning)
        {
            SyncMeshyManagerState();
            return;
        }

        SyncMeshyManagerState();
        try
        {
            while (true)
            {
                string poiName;
                string poiKey;
                lock (_meshyQueueSync)
                {
                    if (_meshyDownloadQueue.Count == 0)
                    {
                        break;
                    }

                    poiName = _meshyDownloadQueue[0];
                    _meshyDownloadQueue.RemoveAt(0);
                    poiKey = NormalizePoiKey(poiName);
                    _activeMeshyQueuePoiKey = poiKey;
                    _meshyQueueStatus = $"Downloading '{poiName}' ({_meshyDownloadQueue.Count} remaining)...";
                    _meshyQueueProgressPercent = 5;
                    _meshyQueueProgressText = $"Priority queue added for '{poiName}'.";
                    _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Downloading...", "downloading");
                }

                SyncMeshyManagerState();
                await ProcessMeshyQueueItemAsync(poiName, poiKey, token);
            }
        }
        catch (OperationCanceledException)
        {
            _meshyManagerStatus = "Meshy queue canceled.";
            lock (_meshyQueueSync)
            {
                _meshyQueueProgressPercent = 0;
                _meshyQueueProgressText = _meshyManagerStatus;
            }
            AppendMeshyManagerLog(_meshyManagerStatus);
        }
        finally
        {
            CancellationTokenSource? queueCtsToDispose = null;
            lock (_meshyQueueSync)
            {
                _meshyQueueRunning = false;
                _meshyManagerBusy = false;
                _activeMeshyQueuePoiKey = null;
                _meshyQueueStatus = _meshyDownloadQueue.Count == 0
                    ? "Queue idle."
                    : $"Queue paused ({_meshyDownloadQueue.Count} pending).";
                _meshyQueueProgressPercent = 0;
                _meshyQueueProgressText = _meshyQueueStatus;
                queueCtsToDispose = _meshyQueueCts;
                _meshyQueueCts = null;
            }

            queueCtsToDispose?.Dispose();
            SyncMeshyManagerState();
        }
    }

    private async Task ProcessMeshyQueueItemAsync(string poiName, string poiKey, CancellationToken token)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        try
        {
            if (HasCachedMeshForPoi(poiKey))
            {
                lock (_meshyQueueSync)
                {
                    _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Already cached.", "cached");
                    _meshyQueuedPoiKeys.Remove(poiKey);
                    _meshyQueueProgressPercent = 100;
                    _meshyQueueProgressText = $"'{poiName}' already cached.";
                }

                _meshyManagerStatus = $"Skipping '{poiName}' because it is already cached.";
                AppendMeshyManagerLog(_meshyManagerStatus);
                SyncMeshyManagerState();
                return;
            }

            var result = await _meshySceneService.GenerateTexturedLandmarkReferencesAsync(
                string.Empty,
                CreateMeshyQueueWeatherStub(),
                new[] { poiName },
                _meshyApiKey,
                token,
                forceRetrySkippedTargets: false,
                progressCallback: message =>
                {
                    lock (_meshyQueueSync)
                    {
                        _meshyQueueProgressPercent = EstimateMeshyQueueProgressPercent(message, _meshyQueueProgressPercent);
                        _meshyQueueProgressText = message;
                    }
                    AppendMeshyManagerLog(message);
                    _meshyManagerStatus = message;
                    SyncMeshyManagerState();
                });

            lock (_meshyQueueSync)
            {
                _meshyQueuedPoiKeys.Remove(poiKey);
            }

            if (result.UsedMeshy && result.GeneratedMeshes.Count > 0)
            {
                lock (_meshyQueueSync)
                {
                    _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Cached.", "cached");
                    _meshyQueueProgressPercent = 100;
                    _meshyQueueProgressText = $"Meshy generated '{poiName}' successfully.";
                }
                _meshyManagerStatus = $"Meshy generated '{poiName}' successfully.";
                AppendMeshyManagerLog(_meshyManagerStatus);
            }
            else
            {
                var errorText = string.IsNullOrWhiteSpace(result.Message)
                    ? $"Meshy returned no model for '{poiName}'."
                    : $"Meshy returned no model for '{poiName}': {result.Message}";
                lock (_meshyQueueSync)
                {
                    _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride(errorText, "error");
                    _meshyQueueProgressPercent = 100;
                    _meshyQueueProgressText = errorText;
                }
                _meshyManagerStatus = errorText;
                AppendMeshyManagerLog(errorText);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_meshyQueueSync)
            {
                _meshyQueuedPoiKeys.Remove(poiKey);
            }

            lock (_meshyQueueSync)
            {
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride("Canceled.", "error");
                _meshyQueueProgressPercent = 100;
                _meshyQueueProgressText = $"Meshy canceled for '{poiName}'.";
            }
            throw;
        }
        catch (Exception ex)
        {
            lock (_meshyQueueSync)
            {
                _meshyQueuedPoiKeys.Remove(poiKey);
                _meshyStatusOverrides[poiKey] = new MeshyRowStatusOverride(
                    $"Meshy generation failed for '{poiName}': {ex.Message}",
                    "error");
                _meshyQueueProgressPercent = 100;
                _meshyQueueProgressText = $"Meshy generation failed for '{poiName}': {ex.Message}";
            }

            var errorText = $"Meshy generation failed for '{poiName}': {ex.Message}";
            _meshyManagerStatus = errorText;
            AppendMeshyManagerLog(errorText);
            LogDebug(errorText, DebugLogCategory.Error);
        }
        finally
        {
            SyncMeshyViewerContext();
        }
    }

    private void OnMeshyModelViewerRequested(object? sender, EventArgs e)
    {
        if (_meshySceneService is null)
        {
            return;
        }

        var pointsOfInterest = _discoveredPointsOfInterest.Count > 0
            ? _discoveredPointsOfInterest
            : (_lastRenderedPayload?.PointsOfInterest ?? Array.Empty<string>());

        if (_meshyModelViewerWindow is null || !_meshyModelViewerWindow.IsLoaded)
        {
            _meshyModelViewerWindow = new MeshyModelViewerWindow(
                _meshySceneService,
                _meshyViewerSettings,
                _locationInput,
                pointsOfInterest,
                _meshyApiKey);
            _meshyModelViewerWindow.ViewerSettingsChanged += OnMeshyViewerSettingsChanged;
            _meshyModelViewerWindow.DebugMessage += OnMeshyViewerDebugMessage;
            ApplyWindowIcon(_meshyModelViewerWindow);
            _meshyModelViewerWindow.Closed += (_, _) =>
            {
                if (_meshyModelViewerWindow is not null)
                {
                    _meshyModelViewerWindow.ViewerSettingsChanged -= OnMeshyViewerSettingsChanged;
                    _meshyModelViewerWindow.DebugMessage -= OnMeshyViewerDebugMessage;
                }

                _meshyModelViewerWindow = null;
            };
            _meshyModelViewerWindow.Show();
            return;
        }

        _meshyModelViewerWindow.SetViewerSettings(_meshyViewerSettings);
        _meshyModelViewerWindow.SetMeshyContext(
            _locationInput,
            pointsOfInterest,
            _meshyApiKey);
        _meshyModelViewerWindow.Show();
        _meshyModelViewerWindow.WindowState = WindowState.Normal;
        _meshyModelViewerWindow.Activate();
    }

    private void OnMeshyViewerSettingsChanged(object? sender, MeshyViewerSettings viewerSettings)
    {
        _meshyViewerSettings = viewerSettings.Normalize();
        PersistSettings();
        LogDebug(
            $"Meshy viewer settings updated: active='{_meshyViewerSettings.ActiveModelRelativePath}', " +
            $"rotateMinutes={_meshyViewerSettings.RotationMinutes:0.###}.");
        SyncMeshyManagerState();

        if (_lastRenderedPayload is null)
        {
            _ = RefreshSceneAsync("Viewer settings changed");
            return;
        }

        _ = ApplyMeshyViewerSettingsAsync("Viewer settings changed");
    }
    private void OnSettingsClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _settingsWindow?.Hide();
    }

    private void OnWallpaperDebugMessage(object? sender, string message)
    {
        var lowered = message?.ToLowerInvariant() ?? string.Empty;
        if (lowered.Contains("default city model", StringComparison.Ordinal) ||
            lowered.Contains("meshy city model", StringComparison.Ordinal) ||
            lowered.Contains("scene source decision", StringComparison.Ordinal))
        {
            var category = (lowered.Contains("failed", StringComparison.Ordinal) ||
                            lowered.Contains("error", StringComparison.Ordinal))
                ? DebugLogCategory.Error
                : DebugLogCategory.System;
            LogDebug($"Wallpaper model trace: {message}", category);
            return;
        }

        LogDebug($"Wallpaper: {message}");
    }

    private void OnMeshyViewerDebugMessage(object? sender, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogDebug($"Meshy viewer: {message}", DebugLogCategory.Meshy);
    }

    private void OnDebugEntryAdded(object? sender, string logLine)
    {
        if (!IsVisibleLogLine(logLine))
        {
            return;
        }

        _settingsWindow?.AppendDebugLine(logLine);
    }

    private void RefreshDebugLogView()
    {
        _settingsWindow?.SetDebugLines(GetVisibleLogLines(300));
    }

    private async Task ApplyGlbOrientationAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _glbOrientation,
                _meshyViewerSettings,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug(
                $"GLB orientation applied ({reason}): rot=({_glbOrientation.RotationXDegrees:0.##}, " +
                $"{_glbOrientation.RotationYDegrees:0.##}, {_glbOrientation.RotationZDegrees:0.##}) " +
                $"scale={_glbOrientation.Scale:0.###} off=({_glbOrientation.OffsetX:0.###}, {_glbOrientation.OffsetY:0.###}, {_glbOrientation.OffsetZ:0.###})");
        }
        catch (Exception ex)
        {
            LogDebug($"GLB orientation apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyTemperatureUnitAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                GetTemperatureUnitToken(_temperatureScale),
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug($"Temperature unit applied ({reason}): {updatedPayload.TemperatureUnit}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Temperature unit apply failed ({reason}): {ex}");
        }
    }

    private Task ApplyWallpaperMonitorAsync(string reason)
    {
        if (_wallpaperWindow is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _wallpaperWindow.SetTargetMonitorDeviceName(_wallpaperMonitorDeviceName);
            LogDebug(
                string.IsNullOrWhiteSpace(_wallpaperMonitorDeviceName)
                    ? $"Wallpaper monitor applied ({reason}): primary."
                    : $"Wallpaper monitor applied ({reason}): {_wallpaperMonitorDeviceName}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper monitor apply failed ({reason}): {ex}");
        }

        return Task.CompletedTask;
    }

    private async Task ApplyWallpaperBackgroundColorAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _wallpaperBackgroundColor,
                GetWallpaperBackgroundImageUrl(),
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug($"Wallpaper background color applied ({reason}): {_wallpaperBackgroundColor}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper background color apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyWallpaperBackgroundImageAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                GetWallpaperBackgroundImageUrl(),
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug(
                $"Wallpaper background media applied ({reason}): " +
                (string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName)
                    ? "none"
                    : _wallpaperBackgroundImageFileName));
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper background media apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyWallpaperBackgroundDisplayModeAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _wallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug($"Wallpaper background display mode applied ({reason}): {_wallpaperBackgroundDisplayMode}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper background display mode apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyWallpaperAnimatedBackgroundAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _useAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug($"Animated wallpaper background applied ({reason}): {(_useAnimatedAiBackground ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Animated wallpaper background apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyWallpaperStatsOverlayAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _showWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug(
                $"Wallpaper stats overlay applied ({reason}): {(_showWallpaperStatsOverlay ? "shown" : "hidden")}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper stats overlay apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyWallpaperTextStyleAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _wallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                _lastRenderedPayload.PointOfInterestMeshes,
                _lastRenderedPayload.GlbOrientation,
                _lastRenderedPayload.MeshyViewer,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug(
                $"Wallpaper text style applied ({reason}): timeFont='{_wallpaperTextStyle.TimeFontFamily}', locationFont='{_wallpaperTextStyle.LocationFontFamily}', " +
                $"time={_wallpaperTextStyle.TimeFontSize:0.#}, location={_wallpaperTextStyle.LocationFontSize:0.#}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Wallpaper text style apply failed ({reason}): {ex}");
        }
    }

    private async Task ApplyMeshyViewerSettingsAsync(string reason)
    {
        if (_wallpaperWindow is null || _lastRenderedPayload is null)
        {
            return;
        }

        try
        {
            var cachedMeshes = _meshySceneService?.GetCachedMeshes() ?? Array.Empty<PointOfInterestMesh>();
            var mergedMeshes = _lastRenderedPayload.PointOfInterestMeshes
                .Concat(cachedMeshes)
                .DistinctBy(mesh => mesh.LocalRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var updatedPayload = new SceneRenderPayload(
                _lastRenderedPayload.LocationQuery,
                _lastRenderedPayload.Coordinates,
                _lastRenderedPayload.Weather,
                _lastRenderedPayload.TemperatureUnit,
                _lastRenderedPayload.WallpaperBackgroundColor,
                _lastRenderedPayload.WallpaperBackgroundImageUrl,
                _lastRenderedPayload.WallpaperBackgroundDisplayMode,
                _lastRenderedPayload.UseAnimatedAiBackground,
                _lastRenderedPayload.ShowWallpaperStatsOverlay,
                _lastRenderedPayload.WallpaperTextStyle,
                _lastRenderedPayload.PointsOfInterest,
                _lastRenderedPayload.PointOfInterestImages,
                mergedMeshes,
                _glbOrientation,
                _meshyViewerSettings,
                _lastRenderedPayload.Scene,
                DateTimeOffset.UtcNow);

            _lastRenderedPayload = updatedPayload;
            await _wallpaperWindow.PushSceneAsync(updatedPayload);
            LogDebug(
                $"Applied Meshy viewer settings to wallpaper ({reason}): " +
                $"active='{_meshyViewerSettings.ActiveModelRelativePath}', rotateMinutes={_meshyViewerSettings.RotationMinutes:0.###}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Apply Meshy viewer settings failed ({reason}): {ex}");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSessionSwitch(sender, e));
            return;
        }

        LogDebug($"Session switch detected: {e.Reason}");
        if (e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
        {
            TriggerSessionRecovery($"Session switch {e.Reason}");
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnPowerModeChanged(sender, e));
            return;
        }

        if (e.Mode == PowerModes.Resume)
        {
            LogDebug("Power resume detected.");
            TriggerSessionRecovery("Power resume");
        }
    }

    private void TriggerSessionRecovery(string reason)
    {
        if (_isExiting)
        {
            return;
        }

        if (_wallpaperWindow is not null)
        {
            _ = _wallpaperWindow.RecoverAfterSessionResumeAsync(reason, forceRendererReload: true);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - _lastSessionRecoveryAtUtc >= TimeSpan.FromSeconds(20))
        {
            _lastSessionRecoveryAtUtc = nowUtc;
            _ = RefreshSceneAsync(reason);
        }
    }

    private void ConfigureTrayIcon()
    {
        _trayIconAsset?.Dispose();
        _trayIconAsset = LoadTrayIcon();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => ShowSettingsWindow());
        menu.Items.Add("Refresh Now", null, (_, _) => _ = RefreshSceneAsync("Tray refresh"));
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconAsset ?? SystemIcons.Application,
            Text = AppBranding.DisplayName,
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettingsWindow();
    }

    private static Icon? LoadTrayIcon()
    {
        try
        {
            using var iconStream = OpenEmbeddedIconStream();
            if (iconStream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            iconStream.CopyTo(buffer);
            buffer.Position = 0;
            using var trayIcon = new Icon(buffer);
            return (Icon)trayIcon.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyWindowIcon(Window window)
    {
        try
        {
            using var iconStream = OpenEmbeddedIconStream();
            if (iconStream is null)
            {
                return;
            }

            using var buffer = new MemoryStream();
            iconStream.CopyTo(buffer);
            buffer.Position = 0;
            var frame = BitmapFrame.Create(
                buffer,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            window.Icon = frame;
        }
        catch
        {
            // Ignore icon assignment failures so the app remains functional.
        }
    }

    private static Stream? OpenEmbeddedIconStream()
    {
        var assembly = typeof(App).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("app-sun-3d.ico", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        return assembly.GetManifestResourceStream(resourceName);
    }

    private async Task RunPeriodicRefreshLoopAsync(CancellationToken cancellationToken)
    {
        if (_periodicTimer is null)
        {
            return;
        }

        try
        {
            while (await _periodicTimer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshSceneAsync("Scheduled refresh", cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            LogDebug("Periodic refresh loop canceled.");
        }
        catch (Exception ex)
        {
            LogDebug($"Periodic refresh loop error: {ex}");
        }
    }

    private async Task RefreshSceneAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!IsOnboardingConfigurationComplete())
        {
            _settingsWindow?.SetStatus(
                "Onboarding incomplete. Wallpaper rendering is paused until all required configuration is complete.");
            return;
        }

        if (_geocodingService is null || _weatherService is null || _pointOfInterestService is null || _pointOfInterestImageService is null || _sceneService is null || _wallpaperWindow is null || _settingsWindow is null)
        {
            return;
        }

        using var refreshCts = CreateAndActivateRefreshToken(cancellationToken);
        var refreshToken = refreshCts.Token;

        await _refreshGate.WaitAsync(refreshToken);

        try
        {
            LogDebug($"Refresh begin ({reason}) for location '{_locationInput}'.");
            _settingsWindow.SetStatus($"Refreshing ({reason})...");

            var coordinates = await _geocodingService.ResolveAsync(_locationInput, refreshToken);
            LogDebug($"Geocoding resolved: {coordinates.DisplayName} ({coordinates.Latitude:0.####}, {coordinates.Longitude:0.####}).");

            var cachedPoiMeshes = _meshySceneService?.GetCachedMeshes() ?? Array.Empty<PointOfInterestMesh>();
            var viewerSettingsForPayload = EnsureValidActiveMeshyModelSelection(cachedPoiMeshes, persistIfChanged: true);
            var cachedPoiNames = cachedPoiMeshes
                .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
                .Select(mesh => mesh.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            LogDebug(cachedPoiMeshes.Count == 0
                ? "Meshy cached models available for scene bootstrap: none."
                : $"Meshy cached models available for scene bootstrap: {cachedPoiMeshes.Count}.");

            var weatherTask = _weatherService.GetSnapshotAsync(coordinates, _weatherApiKey, refreshToken);
            var discoveredPoiTask = _pointOfInterestService.GetNearbyLandmarksAsync(
                coordinates,
                _locationInput,
                _latLngApiKey,
                cachedPoiNames,
                48,
                refreshToken);

            WeatherSnapshot weatherSnapshot;
            try
            {
                weatherSnapshot = await weatherTask;
            }
            catch (Exception weatherException)
            {
                LogDebug(
                    $"Weather fetch failed for {coordinates.DisplayName}: {weatherException.Message}. " +
                    "Using fallback weather snapshot with geocoded location/timezone.",
                    DebugLogCategory.Weather);
                weatherSnapshot = new WeatherSnapshot(
                    LocationName: coordinates.DisplayName,
                    ShortForecast: "Weather unavailable",
                    DetailedForecast: "WeatherAPI data unavailable for this location.",
                    TemperatureF: null,
                    TemperatureC: null,
                    Wind: "Unknown",
                    RelativeHumidityPercent: null,
                    IconUrl: null,
                    TimeZoneId: coordinates.TimeZoneId,
                    Alerts: Array.Empty<WeatherAlertItem>(),
                    CapturedAt: DateTimeOffset.UtcNow);
            }

            if (string.IsNullOrWhiteSpace(weatherSnapshot.TimeZoneId) && !string.IsNullOrWhiteSpace(coordinates.TimeZoneId))
            {
                weatherSnapshot = weatherSnapshot with { TimeZoneId = coordinates.TimeZoneId };
            }

            var selectedTemperature = weatherSnapshot.GetTemperature(_temperatureScale);
            var selectedUnit = _temperatureScale == TemperatureScale.Celsius ? "C" : "F";
            LogDebug(
                $"Weather snapshot: {weatherSnapshot.LocationName} | {weatherSnapshot.ShortForecast} | " +
                $"Temp {selectedTemperature?.ToString("0.#") ?? "N/A"}{selectedUnit} | Wind {weatherSnapshot.Wind} | Alerts {weatherSnapshot.Alerts.Count}");

            var discoveredPointsOfInterest = await discoveredPoiTask;
            discoveredPointsOfInterest =
                _meshySceneService?.ApplyPoiNameOverrides(discoveredPointsOfInterest) ?? discoveredPointsOfInterest;
            _discoveredPointsOfInterest = discoveredPointsOfInterest;
            _settingsWindow.SetOnboardingPois(_discoveredPointsOfInterest);

            var pointsOfInterest = discoveredPointsOfInterest
                .Take(12)
                .ToArray();

            LogDebug(discoveredPointsOfInterest.Count == 0
                ? "Points of interest: none returned from nearby geosearch."
                : $"Points of interest discovered: {string.Join(" | ", discoveredPointsOfInterest)}");
            LogDebug(pointsOfInterest.Length == 0
                ? "Points of interest selected for render: none."
                : $"Points of interest selected for render: {string.Join(" | ", pointsOfInterest)}");
            if (discoveredPointsOfInterest.Count > pointsOfInterest.Length)
            {
                LogDebug(
                    $"Additional POIs available for queueing: {discoveredPointsOfInterest.Count - pointsOfInterest.Length}.");
            }
            SyncMeshyViewerContext(_discoveredPointsOfInterest);
            SyncMeshyManagerState(_discoveredPointsOfInterest);

            var pointOfInterestImagesTask = TryGetPointOfInterestImagesAsync(pointsOfInterest, refreshToken);

            var provisionalPayload = new SceneRenderPayload(
                _locationInput,
                coordinates,
                weatherSnapshot,
                GetTemperatureUnitToken(_temperatureScale),
                _wallpaperBackgroundColor,
                GetWallpaperBackgroundImageUrl(),
                _wallpaperBackgroundDisplayMode,
                _useAnimatedAiBackground,
                _showWallpaperStatsOverlay,
                _wallpaperTextStyle,
                pointsOfInterest,
                Array.Empty<PointOfInterestImage>(),
                cachedPoiMeshes,
                _glbOrientation,
                viewerSettingsForPayload,
                SceneDirectiveService.BuildFallback(weatherSnapshot, pointsOfInterest),
                DateTimeOffset.UtcNow);
            await _wallpaperWindow.PushSceneAsync(provisionalPayload);
            _lastRenderedPayload = provisionalPayload;
            LogDebug(
                "Renderer provisional scene applied while waiting on weather enrichment. " +
                "Using cached models and fallback directive for faster startup.");            var pointOfInterestImages = await pointOfInterestImagesTask;
            LogDebug(pointOfInterestImages.Count == 0
                ? "POI image references: none resolved."
                : $"POI image references: {string.Join(" | ", pointOfInterestImages.Select(image => $"{image.Name} <= {image.SourceUrl}"))}");

            var fastResult = await _sceneService.GenerateFastSceneAsync(
                weatherSnapshot,
                pointsOfInterest,
                pointOfInterestImages,
                refreshToken);
            LogDebug($"Scene generation (procedural): {fastResult.Message}", DebugLogCategory.HighDetail);

            var payload = new SceneRenderPayload(
                _locationInput,
                coordinates,
                weatherSnapshot,
                GetTemperatureUnitToken(_temperatureScale),
                _wallpaperBackgroundColor,
                GetWallpaperBackgroundImageUrl(),
                _wallpaperBackgroundDisplayMode,
                _useAnimatedAiBackground,
                _showWallpaperStatsOverlay,
                _wallpaperTextStyle,
                pointsOfInterest,
                pointOfInterestImages,
                cachedPoiMeshes,
                _glbOrientation,
                viewerSettingsForPayload,
                fastResult.Directive,
                DateTimeOffset.UtcNow);

            await _wallpaperWindow.PushSceneAsync(payload);
            _lastRenderedPayload = payload;
            LogDebug("Renderer update complete.");

            var alertSuffix = weatherSnapshot.Alerts.Count > 0 ? $" | Alerts: {weatherSnapshot.Alerts.Count}" : string.Empty;
            var poiSuffix = pointsOfInterest.Length > 0 ? $" | POIs: {pointsOfInterest.Length}" : string.Empty;
            var modelSuffix = " | Scene: procedural";
            var meshySuffix = string.IsNullOrWhiteSpace(_meshyApiKey)
                ? " | Meshy: API key missing"
                : " | Meshy: enabled";
            _settingsWindow.SetStatus(
                $"Live scene active for {weatherSnapshot.LocationName} | {weatherSnapshot.ShortForecast}{alertSuffix}{poiSuffix}{modelSuffix}{meshySuffix}");
            _settingsWindow.SetLastUpdated(DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            LogDebug($"Refresh canceled ({reason}).");
        }
        catch (Exception ex)
        {
            _settingsWindow.SetStatus($"Refresh failed: {ex.Message}");
            LogDebug($"Refresh failed: {ex}");
        }
        finally
        {
            TryClearActiveRefreshToken(refreshCts);
            _refreshGate.Release();
        }
    }
    private CancellationTokenSource CreateAndActivateRefreshToken(CancellationToken upstreamToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(upstreamToken);
        CancellationTokenSource? previousCts = null;

        lock (_refreshCtsSync)
        {
            if (_activeRefreshCts is not null && !_activeRefreshCts.IsCancellationRequested)
            {
                previousCts = _activeRefreshCts;
            }

            _activeRefreshCts = cts;
        }

        if (previousCts is not null)
        {
            LogDebug("Canceling in-flight refresh in favor of newer request.");
            previousCts.Cancel();
        }

        return cts;
    }

    private void TryClearActiveRefreshToken(CancellationTokenSource token)
    {
        lock (_refreshCtsSync)
        {
            if (ReferenceEquals(_activeRefreshCts, token))
            {
                _activeRefreshCts = null;
            }
        }
    }

    private void SyncMeshyManagerState(IReadOnlyList<string>? pointsOfInterest = null)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        var rows = BuildMeshyManagerRows(pointsOfInterest);
        var logs = _meshyManagerLogs.ToArray();
        var payload = new MeshyManagerStatePayload(
            _meshyManagerStatus,
            _meshyQueueStatus,
            _meshyManagerBusy,
            _meshyViewerSettings.RotationMinutes,
            _meshyQueueProgressPercent,
            _meshyQueueProgressText,
            rows,
            logs);
        _settingsWindow.SetMeshyManagerState(payload);
    }

    private IReadOnlyList<MeshyModelRowState> BuildMeshyManagerRows(IReadOnlyList<string>? pointsOfInterest)
    {
        if (_meshySceneService is null)
        {
            return Array.Empty<MeshyModelRowState>();
        }

        HashSet<string> queuedPoiSnapshot;
        Dictionary<string, MeshyRowStatusOverride> statusOverrideSnapshot;
        string? activeQueuePoiKeySnapshot;
        lock (_meshyQueueSync)
        {
            queuedPoiSnapshot = new HashSet<string>(_meshyQueuedPoiKeys, StringComparer.OrdinalIgnoreCase);
            statusOverrideSnapshot = new Dictionary<string, MeshyRowStatusOverride>(
                _meshyStatusOverrides,
                StringComparer.OrdinalIgnoreCase);
            activeQueuePoiKeySnapshot = _activeMeshyQueuePoiKey;
        }

        var cachedMeshes = _meshySceneService.GetCachedMeshes();
        var cachedByPoiKey = cachedMeshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
            .GroupBy(mesh => NormalizePoiKey(mesh.Name))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var poiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var poi in GetEffectivePoiCandidates(pointsOfInterest))
        {
            var poiKey = NormalizePoiKey(poi);
            if (string.IsNullOrWhiteSpace(poiKey))
            {
                continue;
            }

            if (!poiNames.TryGetValue(poiKey, out var existingName) ||
                poi.Length > existingName.Length)
            {
                poiNames[poiKey] = poi;
            }
        }

        foreach (var cachedMesh in cachedMeshes)
        {
            var poiKey = NormalizePoiKey(cachedMesh.Name);
            if (string.IsNullOrWhiteSpace(poiKey))
            {
                continue;
            }

            if (!poiNames.ContainsKey(poiKey))
            {
                poiNames[poiKey] = cachedMesh.Name;
            }
        }

        var canQueueMeshy = !string.IsNullOrWhiteSpace(_meshyApiKey);
        var normalizedActiveModelPath = NormalizeModelRelativePath(_meshyViewerSettings.ActiveModelRelativePath);
        var rows = new List<MeshyModelRowState>();
        foreach (var cachedMesh in cachedMeshes.OrderBy(mesh => mesh.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isActiveModel = string.Equals(
                NormalizeModelRelativePath(cachedMesh.LocalRelativePath),
                normalizedActiveModelPath,
                StringComparison.OrdinalIgnoreCase);
            rows.Add(new MeshyModelRowState(
                PoiKey: NormalizePoiKey(cachedMesh.Name),
                PoiName: cachedMesh.Name,
                ModelFileName: Path.GetFileName(cachedMesh.LocalRelativePath),
                StatusText: isActiveModel ? "Active" : "Cached",
                StatusKind: "cached",
                IsCachedModel: true,
                IsActiveModel: isActiveModel,
                CanQueue: false,
                CanDownloadNow: false,
                CanDelete: true,
                LocalRelativePath: cachedMesh.LocalRelativePath));
        }

        foreach (var poiEntry in poiNames.OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (cachedByPoiKey.ContainsKey(poiEntry.Key))
            {
                continue;
            }

            var statusText = "Missing";
            var statusKind = "missing";
            var isQueued = queuedPoiSnapshot.Contains(poiEntry.Key);
            if (string.Equals(activeQueuePoiKeySnapshot, poiEntry.Key, StringComparison.OrdinalIgnoreCase))
            {
                statusText = "Downloading...";
                statusKind = "downloading";
            }
            else if (isQueued)
            {
                statusText = "Queued";
                statusKind = "queued";
            }

            if (statusOverrideSnapshot.TryGetValue(poiEntry.Key, out var overrideState))
            {
                statusText = overrideState.Text;
                statusKind = overrideState.Kind;
            }

            rows.Add(new MeshyModelRowState(
                PoiKey: poiEntry.Key,
                PoiName: poiEntry.Value,
                ModelFileName: "(none)",
                StatusText: statusText,
                StatusKind: statusKind,
                IsCachedModel: false,
                IsActiveModel: false,
                CanQueue: canQueueMeshy && !isQueued && !string.Equals(statusKind, "downloading", StringComparison.OrdinalIgnoreCase),
                CanDownloadNow: canQueueMeshy && !isQueued && !string.Equals(statusKind, "downloading", StringComparison.OrdinalIgnoreCase),
                CanDelete: false,
                LocalRelativePath: string.Empty));
        }

        return rows
            .OrderByDescending(row => row.IsCachedModel)
            .ThenBy(row => row.PoiName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ModelFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> GetEffectivePoiCandidates(IReadOnlyList<string>? pointsOfInterest)
    {
        var effective = pointsOfInterest
            ?? (_discoveredPointsOfInterest.Count > 0
                ? _discoveredPointsOfInterest
                : (_lastRenderedPayload?.PointsOfInterest ?? Array.Empty<string>()));
        if (_meshySceneService is null || effective.Count == 0)
        {
            return effective
                .Where(poi => !string.IsNullOrWhiteSpace(poi))
                .Select(poi => poi.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return _meshySceneService
            .ApplyPoiNameOverrides(effective)
            .Where(poi => !string.IsNullOrWhiteSpace(poi))
            .Select(poi => poi.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool HasCachedMeshForPoi(string poiKey)
    {
        if (_meshySceneService is null || string.IsNullOrWhiteSpace(poiKey))
        {
            return false;
        }

        return _meshySceneService
            .GetCachedMeshes()
            .Any(mesh => string.Equals(NormalizePoiKey(mesh.Name), poiKey, StringComparison.OrdinalIgnoreCase));
    }

    private static double EstimateMeshyQueueProgressPercent(string message, double currentPercent)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Math.Max(0, Math.Min(100, currentPercent));
        }

        var lower = message.Trim().ToLowerInvariant();
        double target = currentPercent;

        if (lower.Contains("priority queue added", StringComparison.Ordinal) ||
            lower.Contains("queued '", StringComparison.Ordinal))
        {
            target = 5;
        }
        else if (lower.Contains("text-to-3d requested", StringComparison.Ordinal))
        {
            target = 10;
        }
        else if (lower.Contains("text preview submit", StringComparison.Ordinal))
        {
            target = 15;
        }
        else if (lower.Contains("status: pending", StringComparison.Ordinal))
        {
            target = currentPercent >= 65 ? 70 : 22;
        }
        else if (lower.Contains("status: in_progress", StringComparison.Ordinal))
        {
            target = currentPercent >= 65 ? 78 : 35;
        }
        else if (lower.Contains("status: succeeded", StringComparison.Ordinal))
        {
            target = currentPercent >= 70 ? 86 : 55;
        }
        else if (lower.Contains("completed with", StringComparison.Ordinal))
        {
            target = currentPercent >= 86 ? 88 : 60;
        }
        else if (lower.Contains("text refine submit", StringComparison.Ordinal))
        {
            target = 65;
        }
        else if (lower.Contains("download image", StringComparison.Ordinal))
        {
            target = 90;
        }
        else if (lower.Contains("image cached", StringComparison.Ordinal) ||
                 lower.Contains("image download skipped", StringComparison.Ordinal))
        {
            target = 92;
        }
        else if (lower.Contains("download model", StringComparison.Ordinal))
        {
            target = 95;
        }
        else if (lower.Contains("model cached", StringComparison.Ordinal))
        {
            target = 98;
        }
        else if (lower.Contains("model skipped", StringComparison.Ordinal) ||
                 lower.Contains("model download skipped", StringComparison.Ordinal))
        {
            target = 96;
        }
        else if (lower.Contains("cache manifest written", StringComparison.Ordinal) ||
                 (lower.Contains("generated '", StringComparison.Ordinal) && lower.Contains("successfully", StringComparison.Ordinal)))
        {
            target = 100;
        }
        else if (lower.Contains("failed", StringComparison.Ordinal) ||
                 lower.Contains("error", StringComparison.Ordinal) ||
                 lower.Contains("canceled", StringComparison.Ordinal))
        {
            target = 100;
        }

        return Math.Max(Math.Max(0, currentPercent), Math.Min(100, target));
    }

    private bool IsPoiNameInUse(string poiKey)
    {
        if (string.IsNullOrWhiteSpace(poiKey))
        {
            return false;
        }

        if (HasCachedMeshForPoi(poiKey))
        {
            return true;
        }

        return GetEffectivePoiCandidates(null)
            .Any(poi => string.Equals(NormalizePoiKey(poi), poiKey, StringComparison.OrdinalIgnoreCase));
    }

    private void AppendMeshyManagerLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_meshyQueueSync)
        {
            _meshyManagerLogs.Enqueue(line);
            while (_meshyManagerLogs.Count > MaxMeshyManagerLogLines)
            {
                _meshyManagerLogs.Dequeue();
            }
        }

        _settingsWindow?.AppendMeshyManagerLog(line);
        LogDebug($"Meshy manager: {message}", DebugLogCategory.Meshy);
    }

    private WeatherSnapshot CreateMeshyQueueWeatherStub()
    {
        var locationName = string.IsNullOrWhiteSpace(_locationInput) ? "Unknown" : _locationInput;
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

    private static string NormalizePoiKey(string poiName)
    {
        if (string.IsNullOrWhiteSpace(poiName))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            poiName
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeModelRelativePath(string localRelativePath)
    {
        return string.IsNullOrWhiteSpace(localRelativePath)
            ? string.Empty
            : localRelativePath.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static bool TryDecodeMeshyImportDataUrl(string dataUrl, out string mimeType, out byte[] bytes)
    {
        mimeType = string.Empty;
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return false;
        }

        var trimmed = dataUrl.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex <= 0 || commaIndex + 1 >= trimmed.Length)
        {
            return false;
        }

        var metadata = trimmed[..commaIndex];
        var payload = trimmed[(commaIndex + 1)..];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var semicolonIndex = metadata.IndexOf(';');
        if (semicolonIndex <= 5)
        {
            return false;
        }

        mimeType = metadata[5..semicolonIndex].Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(mimeType) &&
            mimeType is not ("model/gltf-binary" or "application/octet-stream" or "model/octet-stream"))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private void SyncMeshyViewerContext(IReadOnlyList<string>? pointsOfInterest = null)
    {
        var effectivePoints = pointsOfInterest
            ?? (_discoveredPointsOfInterest.Count > 0
                ? _discoveredPointsOfInterest
                : (_lastRenderedPayload?.PointsOfInterest ?? Array.Empty<string>()));

        SyncMeshyManagerState(effectivePoints);

        if (_meshyModelViewerWindow is null || !_meshyModelViewerWindow.IsLoaded)
        {
            return;
        }

        _meshyModelViewerWindow.SetMeshyContext(_locationInput, effectivePoints, _meshyApiKey);
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        LogDebug("Exiting application.");
        _settingsWindow?.Close();
        _wallpaperWindow?.Close();
        Shutdown();
    }

    private void EnsureOnboardingGuideVisible()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        if (IsOnboardingConfigurationComplete())
        {
            return;
        }

        _settingsWindow.SetStatus(
            "Onboarding required: set location, Weather API key, Meshy API key, LatLng API key, and finalize scene settings.");
        _settingsWindow.ShowHelpGuide();
        LogDebug(
            "Onboarding guide shown because setup is incomplete (location/API keys/model selection).",
            DebugLogCategory.System);
    }

    private async Task<bool> EnsureWallpaperStartedAsync(string reason)
    {
        if (_wallpaperWindow is not null)
        {
            return true;
        }

        if (_settingsWindow is null)
        {
            return false;
        }

        try
        {
            _wallpaperWindow = new WallpaperWindow();
            _wallpaperWindow.DebugMessage += OnWallpaperDebugMessage;
            _wallpaperWindow.SetTargetMonitorDeviceName(_wallpaperMonitorDeviceName);
            _wallpaperWindow.Show();
            await _wallpaperWindow.WaitUntilReadyAsync();
            LogDebug($"Wallpaper renderer is ready ({reason}).");

            if (_wallpaperWindow.LastAttachResult is WallpaperAttachResult attachResult)
            {
                LogDebug(
                    $"Attach summary: {attachResult.Message} | Target {DesktopWallpaperHost.DescribeHandle(attachResult.TargetParentHandle)} " +
                    $"({attachResult.TargetParentClass}) | Actual {DesktopWallpaperHost.DescribeHandle(attachResult.ActualParentHandle)} " +
                    $"({attachResult.ActualParentClass})");
                _settingsWindow.SetStatus(
                    attachResult.IsAttached
                        ? $"Wallpaper attached ({attachResult.Strategy})."
                        : $"Wallpaper attach issue: {attachResult.Message}");
            }

            await PushStartupBootstrapPayloadAsync(reason);
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to start wallpaper window ({reason}): {ex}");
            _settingsWindow.SetStatus($"Wallpaper start failed: {ex.Message}");

            if (_wallpaperWindow is not null)
            {
                _wallpaperWindow.DebugMessage -= OnWallpaperDebugMessage;
                _wallpaperWindow.Close();
                _wallpaperWindow = null;
            }

            return false;
        }
    }

    private async Task PushStartupBootstrapPayloadAsync(string reason)
    {
        if (_wallpaperWindow is null || _meshySceneService is null)
        {
            return;
        }

        try
        {
            var payload = BuildStartupBootstrapPayload();
            await _wallpaperWindow.PushSceneAsync(payload);
            _lastRenderedPayload = payload;
            _settingsWindow?.SetStatus(
                $"Initializing live data for {payload.Weather.LocationName}... cached models loaded.");
            LogDebug(
                $"Startup bootstrap payload applied ({reason}) with {payload.PointOfInterestMeshes.Count} cached mesh(es) " +
                $"and active model '{payload.MeshyViewer.ActiveModelRelativePath}'.");
        }
        catch (Exception ex)
        {
            LogDebug($"Startup bootstrap payload failed ({reason}): {ex.Message}", DebugLogCategory.Error);
        }
    }

    private SceneRenderPayload BuildStartupBootstrapPayload()
    {
        var locationName = string.IsNullOrWhiteSpace(_locationInput) ? "Configured location" : _locationInput.Trim();
        var coordinates = new GeoCoordinates(
            Latitude: 0,
            Longitude: 0,
            DisplayName: locationName,
            TimeZoneId: TimeZoneInfo.Local.Id);
        var weather = new WeatherSnapshot(
            LocationName: locationName,
            ShortForecast: "Initializing weather...",
            DetailedForecast: "Loading live weather and scene details.",
            TemperatureF: null,
            TemperatureC: null,
            Wind: "Unknown",
            RelativeHumidityPercent: null,
            IconUrl: null,
            TimeZoneId: TimeZoneInfo.Local.Id,
            Alerts: Array.Empty<WeatherAlertItem>(),
            CapturedAt: DateTimeOffset.UtcNow);

        var cachedPoiMeshes = (_meshySceneService?.GetCachedMeshes() ?? Array.Empty<PointOfInterestMesh>()).ToArray();
        var viewerSettings = EnsureValidActiveMeshyModelSelection(cachedPoiMeshes, persistIfChanged: true);
        var pointsOfInterest = cachedPoiMeshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
            .Select(mesh => mesh.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return new SceneRenderPayload(
            LocationQuery: _locationInput,
            Coordinates: coordinates,
            Weather: weather,
            TemperatureUnit: GetTemperatureUnitToken(_temperatureScale),
            WallpaperBackgroundColor: _wallpaperBackgroundColor,
            WallpaperBackgroundImageUrl: GetWallpaperBackgroundImageUrl(),
            WallpaperBackgroundDisplayMode: _wallpaperBackgroundDisplayMode,
            UseAnimatedAiBackground: _useAnimatedAiBackground,
            ShowWallpaperStatsOverlay: _showWallpaperStatsOverlay,
            WallpaperTextStyle: _wallpaperTextStyle,
            PointsOfInterest: pointsOfInterest,
            PointOfInterestImages: Array.Empty<PointOfInterestImage>(),
            PointOfInterestMeshes: cachedPoiMeshes,
            GlbOrientation: _glbOrientation,
            MeshyViewer: viewerSettings,
            Scene: SceneDirectiveService.BuildFallback(weather, pointsOfInterest),
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    private MeshyViewerSettings EnsureValidActiveMeshyModelSelection(
        IReadOnlyList<PointOfInterestMesh> cachedMeshes,
        bool persistIfChanged)
    {
        var normalized = _meshyViewerSettings.Normalize();
        var normalizedActivePath = NormalizeModelRelativePath(normalized.ActiveModelRelativePath);
        var availablePaths = cachedMeshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.LocalRelativePath))
            .Select(mesh => NormalizeModelRelativePath(mesh.LocalRelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string resolvedActivePath;
        if (availablePaths.Length == 0)
        {
            resolvedActivePath = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedActivePath) &&
                 availablePaths.Contains(normalizedActivePath, StringComparer.OrdinalIgnoreCase))
        {
            resolvedActivePath = availablePaths.First(path =>
                string.Equals(path, normalizedActivePath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            resolvedActivePath = availablePaths[0];
        }

        var updated = normalized with { ActiveModelRelativePath = resolvedActivePath };
        if (updated == _meshyViewerSettings)
        {
            return updated;
        }

        _meshyViewerSettings = updated;
        if (persistIfChanged)
        {
            PersistSettings();
            SyncMeshyManagerState();
        }

        return updated;
    }

    private async Task<IReadOnlyList<PointOfInterestImage>> TryGetPointOfInterestImagesAsync(
        IReadOnlyList<string> pointsOfInterest,
        CancellationToken cancellationToken)
    {
        if (_pointOfInterestImageService is null || pointsOfInterest.Count == 0)
        {
            return Array.Empty<PointOfInterestImage>();
        }

        try
        {
            return await _pointOfInterestImageService.GetReferenceImagesAsync(pointsOfInterest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDebug(
                $"POI image fetch failed: {ex.Message}. Continuing with cached meshes and procedural assets.",
                DebugLogCategory.Error);
            return Array.Empty<PointOfInterestImage>();
        }
    }

    private void EnsurePeriodicRefreshLoopRunning()
    {
        if (_updateLoopCts is not null && !_updateLoopCts.IsCancellationRequested && _periodicTimer is not null)
        {
            return;
        }

        _updateLoopCts = new CancellationTokenSource();
        _periodicTimer?.Dispose();
        _periodicTimer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        _ = RunPeriodicRefreshLoopAsync(_updateLoopCts.Token);
        LogDebug("Periodic refresh loop started.");
    }

    private async Task EnsureWallpaperStartedAndRefreshAsync(string reason)
    {
        var started = await EnsureWallpaperStartedAsync(reason);
        if (!started)
        {
            return;
        }

        EnsurePeriodicRefreshLoopRunning();
        await RefreshSceneAsync(reason);
    }

    private string SaveWallpaperBackgroundImage(WallpaperBackgroundImageChangeRequest request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("Wallpaper background media request is empty.");
        }

        if (!TryDecodeWallpaperBackgroundDataUrl(request.DataUrl, out var mimeType, out var bytes))
        {
            throw new InvalidOperationException("Unable to decode selected media. Use a valid image or video file.");
        }

        Directory.CreateDirectory(_wallpaperAssetsRootPath);
        var extension = ResolveWallpaperBackgroundExtension(mimeType, request.FileName);
        var fileName = $"wallpaper-background-media{extension}";
        foreach (var existingFile in Directory.EnumerateFiles(_wallpaperAssetsRootPath, "wallpaper-background*.*"))
        {
            if (!string.Equals(Path.GetFileName(existingFile), fileName, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(existingFile);
            }
        }
        var fullPath = Path.Combine(_wallpaperAssetsRootPath, fileName);
        File.WriteAllBytes(fullPath, bytes);
        return fileName;
    }

    private string GetWallpaperBackgroundImageUrl()
    {
        var fileName = NormalizeWallpaperBackgroundImageFileName(_wallpaperBackgroundImageFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var fullPath = Path.Combine(_wallpaperAssetsRootPath, fileName);
        if (!File.Exists(fullPath))
        {
            return string.Empty;
        }

        var cacheBuster = File.GetLastWriteTimeUtc(fullPath).Ticks;
        return $"https://{WallpaperWindow.WallpaperAssetsVirtualHostName}/{Uri.EscapeDataString(fileName)}?v={cacheBuster}";
    }

    private bool IsOnboardingConfigurationComplete()
    {
        return !string.IsNullOrWhiteSpace(_locationInput) &&
               !string.IsNullOrWhiteSpace(_meshyApiKey) &&
               !string.IsNullOrWhiteSpace(_weatherApiKey) &&
               !string.IsNullOrWhiteSpace(_latLngApiKey) ;
    }

    private void LogDebug(string message, DebugLogCategory? category = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var resolvedCategory = category ?? ClassifyLogCategory(message);
        _debugLog.Log($"[{resolvedCategory}] {message}");
    }

    private IReadOnlyList<string> GetVisibleLogLines(int maxEntries)
    {
        return _debugLog
            .GetRecentEntries(1000)
            .Where(IsVisibleLogLine)
            .TakeLast(maxEntries)
            .ToArray();
    }

    private bool IsVisibleLogLine(string logLine)
    {
        var payload = ExtractLogPayload(logLine);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var category = TryExtractTaggedCategory(payload, out var taggedCategory)
            ? taggedCategory
            : ClassifyLogCategory(payload);
        return _logFilters.IsEnabled(category);
    }

    private static string ExtractLogPayload(string logLine)
    {
        if (string.IsNullOrWhiteSpace(logLine))
        {
            return string.Empty;
        }

        var separatorIndex = logLine.IndexOf(" | ", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 3 >= logLine.Length)
        {
            return logLine;
        }

        return logLine[(separatorIndex + 3)..];
    }

    private static bool TryExtractTaggedCategory(string payload, out DebugLogCategory category)
    {
        category = DebugLogCategory.System;
        if (!payload.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var closeBracketIndex = payload.IndexOf(']');
        if (closeBracketIndex <= 1)
        {
            return false;
        }

        var categoryToken = payload[1..closeBracketIndex];
        return Enum.TryParse(categoryToken, ignoreCase: true, out category);
    }

    private static DebugLogCategory ClassifyLogCategory(string message)
    {
        var lowered = message.ToLowerInvariant();
        if (lowered.StartsWith("wallpaper:", StringComparison.Ordinal) ||
            lowered.Contains("renderer ", StringComparison.Ordinal) ||
            lowered.Contains("desktop attach", StringComparison.Ordinal) ||
            lowered.Contains("applied bounds |", StringComparison.Ordinal) ||
            lowered.Contains("viewport", StringComparison.Ordinal))
        {
            return DebugLogCategory.RendererDebug;
        }

        if (lowered.Contains("meshy", StringComparison.Ordinal))
        {
            return DebugLogCategory.Meshy;
        }

        if (lowered.Contains("scene generation", StringComparison.Ordinal))
        {
            return DebugLogCategory.HighDetail;
        }

        if (lowered.Contains("latlng", StringComparison.Ordinal) ||
            lowered.Contains("poi resolver", StringComparison.Ordinal))
        {
            return DebugLogCategory.LatLng;
        }

        if (lowered.Contains("weatherapi", StringComparison.Ordinal) ||
            lowered.Contains("weather api", StringComparison.Ordinal) ||
            lowered.Contains("weather snapshot", StringComparison.Ordinal) ||
            lowered.Contains("geocoding resolved", StringComparison.Ordinal) ||
            lowered.Contains("points of interest", StringComparison.Ordinal) ||
            lowered.Contains("poi image references", StringComparison.Ordinal) ||
            lowered.Contains("alerts", StringComparison.Ordinal))
        {
            return DebugLogCategory.Weather;
        }

        if (lowered.Contains("high-detail", StringComparison.Ordinal) ||
            lowered.Contains("high detail", StringComparison.Ordinal) ||
            lowered.Contains("high-resolution", StringComparison.Ordinal))
        {
            return DebugLogCategory.HighDetail;
        }

        if (lowered.Contains("failed", StringComparison.Ordinal) ||
            lowered.Contains("error", StringComparison.Ordinal) ||
            lowered.Contains("exception", StringComparison.Ordinal))
        {
            return DebugLogCategory.Error;
        }

        return DebugLogCategory.System;
    }

    private void LoadPersistedSettings()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            var saved = JsonSerializer.Deserialize<PersistedSettingsDto>(json, SettingsSerializationOptions);
            if (saved is null)
            {
                return;
            }

            _locationInput = saved.LocationQuery?.Trim() ?? string.Empty;

            _meshyApiKey = UnprotectSecret(saved.MeshyApiKeyProtected);
            _weatherApiKey = UnprotectSecret(saved.WeatherApiKeyProtected);
            _latLngApiKey = UnprotectSecret(saved.LatLngApiKeyProtected);
            _temperatureScale = Enum.TryParse<TemperatureScale>(saved.TemperatureScale, ignoreCase: true, out var persistedScale)
                ? persistedScale
                : TemperatureScale.Fahrenheit;
            _wallpaperMonitorDeviceName = NormalizeWallpaperMonitorDeviceName(saved.WallpaperMonitorDeviceName);
            _wallpaperBackgroundColor = NormalizeHexColor(saved.WallpaperBackgroundColor, _wallpaperBackgroundColor);
            _wallpaperBackgroundImageFileName = NormalizeWallpaperBackgroundImageFileName(saved.WallpaperBackgroundImageFileName);
            _wallpaperBackgroundDisplayMode = NormalizeWallpaperBackgroundDisplayMode(saved.WallpaperBackgroundDisplayMode);
            _useAnimatedAiBackground = saved.UseAnimatedAiBackground;
            _showWallpaperStatsOverlay = saved.ShowWallpaperStatsOverlay;
            var legacyFontFamily = WallpaperTextStyleSettings.NormalizeFontFamily(saved.WallpaperFontFamily);
            _wallpaperTextStyle = new WallpaperTextStyleSettings(
                TimeFontFamily: ResolvePersistedFont(saved.WallpaperTimeFontFamily, legacyFontFamily),
                LocationFontFamily: ResolvePersistedFont(saved.WallpaperLocationFontFamily, legacyFontFamily),
                DateFontFamily: ResolvePersistedFont(saved.WallpaperDateFontFamily, legacyFontFamily),
                TemperatureFontFamily: ResolvePersistedFont(saved.WallpaperTemperatureFontFamily, legacyFontFamily),
                SummaryFontFamily: ResolvePersistedFont(saved.WallpaperSummaryFontFamily, legacyFontFamily),
                PoiFontFamily: ResolvePersistedFont(saved.WallpaperPoiFontFamily, legacyFontFamily),
                AlertsFontFamily: ResolvePersistedFont(saved.WallpaperAlertsFontFamily, legacyFontFamily),
                TimeFontSize: saved.WallpaperTimeFontSize,
                LocationFontSize: saved.WallpaperLocationFontSize,
                DateFontSize: saved.WallpaperDateFontSize,
                TemperatureFontSize: saved.WallpaperTemperatureFontSize,
                SummaryFontSize: saved.WallpaperSummaryFontSize,
                PoiFontSize: saved.WallpaperPoiFontSize,
                AlertsFontSize: saved.WallpaperAlertsFontSize).Normalize();
            if (!string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName))
            {
                var persistedImagePath = Path.Combine(_wallpaperAssetsRootPath, _wallpaperBackgroundImageFileName);
                if (!File.Exists(persistedImagePath))
                {
                    _wallpaperBackgroundImageFileName = string.Empty;
                }
            }
            _showDebugLogPane = saved.ShowDebugLogPane;
            _meshyViewerSettings = new MeshyViewerSettings(
                saved.MeshyViewerActiveModelRelativePath,
                saved.MeshyViewerRotationMinutes).Normalize();            _logFilters = new LogFilterSettings(
                ShowSystem: saved.ShowSystemLogs,
                ShowWeather: saved.ShowWeatherLogs,
                ShowLatLng: saved.ShowLatLngLogs,
                ShowMeshy: saved.ShowMeshyLogs,
                ShowHighDetail: saved.ShowHighDetailLogs,
                ShowRendererDebug: saved.ShowRendererDebugLogs,
                ShowErrors: saved.ShowErrorLogs);
            _glbOrientation = new GlbOrientationSettings(
                saved.GlbRotationXDegrees,
                saved.GlbRotationYDegrees,
                saved.GlbRotationZDegrees,
                saved.GlbScale,
                saved.GlbOffsetX,
                saved.GlbOffsetY,
                saved.GlbOffsetZ).Normalize();
            var apiKeyState = string.IsNullOrWhiteSpace(_meshyApiKey) ? "empty" : "loaded";
            var weatherApiKeyState = string.IsNullOrWhiteSpace(_weatherApiKey) ? "empty" : "loaded";
            var latLngState = string.IsNullOrWhiteSpace(_latLngApiKey) ? "empty" : "loaded";
            var wallpaperMonitorState = string.IsNullOrWhiteSpace(_wallpaperMonitorDeviceName)
                ? "primary"
                : _wallpaperMonitorDeviceName;
            var wallpaperImageState = string.IsNullOrWhiteSpace(_wallpaperBackgroundImageFileName)
                ? "none"
                : _wallpaperBackgroundImageFileName;
            var wallpaperDisplayModeState = _wallpaperBackgroundDisplayMode;
            var animatedBackgroundState = _useAnimatedAiBackground ? "enabled" : "disabled";
            var wallpaperFontState = $"time={_wallpaperTextStyle.TimeFontFamily}, location={_wallpaperTextStyle.LocationFontFamily}";
            LogDebug(
                $"Loaded persisted settings. Location '{_locationInput}', Meshy API key {apiKeyState}. " +
                $"Weather API key {weatherApiKeyState}. " +
                $"LatLng API key {latLngState}. Wallpaper monitor {wallpaperMonitorState}. Wallpaper background {_wallpaperBackgroundColor}. " +
                $"Wallpaper image {wallpaperImageState}. Wallpaper display mode {wallpaperDisplayModeState}. Animated background {animatedBackgroundState}. " +
                $"Wallpaper font {wallpaperFontState}. " +
                $"Scene profile active.");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to load persisted settings: {ex.Message}");
        }
    }

    private void PersistSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var saved = new PersistedSettingsDto
            {
                LocationQuery = _locationInput,
                MeshyApiKeyProtected = ProtectSecret(_meshyApiKey),
                WeatherApiKeyProtected = ProtectSecret(_weatherApiKey),
                LatLngApiKeyProtected = ProtectSecret(_latLngApiKey),
                TemperatureScale = _temperatureScale.ToString(),
                WallpaperMonitorDeviceName = _wallpaperMonitorDeviceName,
                WallpaperBackgroundColor = _wallpaperBackgroundColor,
                WallpaperBackgroundImageFileName = _wallpaperBackgroundImageFileName,
                WallpaperBackgroundDisplayMode = _wallpaperBackgroundDisplayMode,
                UseAnimatedAiBackground = _useAnimatedAiBackground,
                WallpaperFontFamily = _wallpaperTextStyle.TimeFontFamily,
                WallpaperTimeFontFamily = _wallpaperTextStyle.TimeFontFamily,
                WallpaperLocationFontFamily = _wallpaperTextStyle.LocationFontFamily,
                WallpaperDateFontFamily = _wallpaperTextStyle.DateFontFamily,
                WallpaperTemperatureFontFamily = _wallpaperTextStyle.TemperatureFontFamily,
                WallpaperSummaryFontFamily = _wallpaperTextStyle.SummaryFontFamily,
                WallpaperPoiFontFamily = _wallpaperTextStyle.PoiFontFamily,
                WallpaperAlertsFontFamily = _wallpaperTextStyle.AlertsFontFamily,
                WallpaperTimeFontSize = _wallpaperTextStyle.TimeFontSize,
                WallpaperLocationFontSize = _wallpaperTextStyle.LocationFontSize,
                WallpaperDateFontSize = _wallpaperTextStyle.DateFontSize,
                WallpaperTemperatureFontSize = _wallpaperTextStyle.TemperatureFontSize,
                WallpaperSummaryFontSize = _wallpaperTextStyle.SummaryFontSize,
                WallpaperPoiFontSize = _wallpaperTextStyle.PoiFontSize,
                WallpaperAlertsFontSize = _wallpaperTextStyle.AlertsFontSize,
                ShowDebugLogPane = _showDebugLogPane,
                MeshyViewerActiveModelRelativePath = _meshyViewerSettings.ActiveModelRelativePath,
                MeshyViewerRotationMinutes = _meshyViewerSettings.RotationMinutes,
                ShowWallpaperStatsOverlay = _showWallpaperStatsOverlay,                ShowSystemLogs = _logFilters.ShowSystem,
                ShowWeatherLogs = _logFilters.ShowWeather,
                ShowLatLngLogs = _logFilters.ShowLatLng,                ShowMeshyLogs = _logFilters.ShowMeshy,
                ShowHighDetailLogs = _logFilters.ShowHighDetail,
                ShowRendererDebugLogs = _logFilters.ShowRendererDebug,
                ShowErrorLogs = _logFilters.ShowErrors,
                GlbRotationXDegrees = _glbOrientation.RotationXDegrees,
                GlbRotationYDegrees = _glbOrientation.RotationYDegrees,
                GlbRotationZDegrees = _glbOrientation.RotationZDegrees,
                GlbScale = _glbOrientation.Scale,
                GlbOffsetX = _glbOrientation.OffsetX,
                GlbOffsetY = _glbOrientation.OffsetY,
                GlbOffsetZ = _glbOrientation.OffsetZ
            };
            var json = JsonSerializer.Serialize(saved, SettingsSerializationOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            LogDebug($"Persist settings failed: {ex.Message}");
        }
    }

    private static string ProtectSecret(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        var inputBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(inputBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string UnprotectSecret(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            return string.Empty;
        }

        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }




    private static bool HasSwitch(string[] args, string value)
    {
        if (args is null || args.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return args.Any(arg => string.Equals(arg?.Trim(), value, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryCleanupUserDataArtifacts()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var brandedRoot = Path.Combine(localAppData, AppBranding.SafeName);
            var legacyRoot = Path.Combine(localAppData, AppBranding.LegacySafeName);
            TryDeleteDirectory(brandedRoot);
            if (!string.Equals(brandedRoot, legacyRoot, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(legacyRoot);
            }
        }



    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort reset cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort reset cleanup.
        }
    }
    private static string GetTemperatureUnitToken(TemperatureScale scale)
    {
        return scale == TemperatureScale.Celsius ? "C" : "F";
    }

    private static string NormalizeWallpaperMonitorDeviceName(string? monitorDeviceName)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            return string.Empty;
        }

        var normalized = DesktopWallpaperHost.NormalizeMonitorDeviceName(monitorDeviceName);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    private static string NormalizeWallpaperBackgroundImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var normalized = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".mp4" or ".webm" or ".ogg" or ".mov" or ".m4v" or ".mkv" => normalized,
            _ => string.Empty
        };
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

    private static string ResolvePersistedFont(string? candidate, string legacyFallback)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return WallpaperTextStyleSettings.NormalizeFontFamily(candidate);
        }

        return WallpaperTextStyleSettings.NormalizeFontFamily(legacyFallback);
    }

    private static bool TryDecodeWallpaperBackgroundDataUrl(string dataUrl, out string mimeType, out byte[] bytes)
    {
        mimeType = string.Empty;
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return false;
        }

        var trimmed = dataUrl.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex <= 0 || commaIndex + 1 >= trimmed.Length)
        {
            return false;
        }

        var metadata = trimmed[..commaIndex];
        var payload = trimmed[(commaIndex + 1)..];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var slashIndex = metadata.IndexOf('/');
        var semicolonIndex = metadata.IndexOf(';');
        if (slashIndex < 0 || semicolonIndex <= slashIndex)
        {
            return false;
        }

        mimeType = metadata[5..semicolonIndex].Trim();
        var normalizedMime = mimeType.ToLowerInvariant();
        if (normalizedMime is not ("image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/bmp" or
                                   "video/mp4" or "video/webm" or "video/ogg" or "video/quicktime" or "video/x-m4v" or
                                   "video/x-matroska" or "video/matroska" or "video/mkv"))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static string ResolveWallpaperBackgroundExtension(string mimeType, string fileName)
    {
        var normalizedName = Path.GetFileName(fileName ?? string.Empty);
        var fromName = Path.GetExtension(normalizedName).ToLowerInvariant();
        if (fromName is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".mp4" or ".webm" or ".ogg" or ".mov" or ".m4v" or ".mkv")
        {
            return fromName;
        }

        var normalizedMime = (mimeType ?? string.Empty).ToLowerInvariant();
        return normalizedMime switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/ogg" => ".ogg",
            "video/quicktime" => ".mov",
            "video/x-m4v" => ".m4v",
            "video/x-matroska" => ".mkv",
            "video/matroska" => ".mkv",
            "video/mkv" => ".mkv",
            _ => ".png"
        };
    }

    private static string NormalizeHexColor(string? value, string fallback)
    {
        var fallbackValue = string.IsNullOrWhiteSpace(fallback) ? "#7AA7D8" : fallback.Trim();
        if (!fallbackValue.StartsWith('#'))
        {
            fallbackValue = $"#{fallbackValue}";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackValue.ToUpperInvariant();
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

        return fallbackValue.ToUpperInvariant();
    }

    private static bool IsHexChar(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }

    private sealed record MeshyRowStatusOverride(string Text, string Kind);

    private sealed class PersistedSettingsDto
    {
        public string LocationQuery { get; init; } = string.Empty;

        public string MeshyApiKeyProtected { get; init; } = string.Empty;

        public string WeatherApiKeyProtected { get; init; } = string.Empty;

        public string LatLngApiKeyProtected { get; init; } = string.Empty;

        public string TemperatureScale { get; init; } = nameof(Malie.Models.TemperatureScale.Fahrenheit);

        public string WallpaperMonitorDeviceName { get; init; } = string.Empty;

        public string WallpaperBackgroundColor { get; init; } = "#7AA7D8";

        public string WallpaperBackgroundImageFileName { get; init; } = string.Empty;

        public string WallpaperBackgroundDisplayMode { get; init; } = "Fill";

        public bool UseAnimatedAiBackground { get; init; } = false;

        public bool ShowWallpaperStatsOverlay { get; init; } = true;

        public string WallpaperFontFamily { get; init; } = "Segoe UI";

        public string WallpaperTimeFontFamily { get; init; } = "Segoe UI";

        public string WallpaperLocationFontFamily { get; init; } = "Segoe UI";

        public string WallpaperDateFontFamily { get; init; } = "Segoe UI";

        public string WallpaperTemperatureFontFamily { get; init; } = "Segoe UI";

        public string WallpaperSummaryFontFamily { get; init; } = "Segoe UI";

        public string WallpaperPoiFontFamily { get; init; } = "Segoe UI";

        public string WallpaperAlertsFontFamily { get; init; } = "Segoe UI";

        public double WallpaperTimeFontSize { get; init; } = 58;

        public double WallpaperLocationFontSize { get; init; } = 54;

        public double WallpaperDateFontSize { get; init; } = 14;

        public double WallpaperTemperatureFontSize { get; init; } = 34;

        public double WallpaperSummaryFontSize { get; init; } = 14;

        public double WallpaperPoiFontSize { get; init; } = 14;

        public double WallpaperAlertsFontSize { get; init; } = 14;

        public bool ShowDebugLogPane { get; init; } = true;

        public string MeshyViewerActiveModelRelativePath { get; init; } = string.Empty;

        public double MeshyViewerRotationMinutes { get; init; } = 0;
        public bool ShowSystemLogs { get; init; } = true;

        public bool ShowWeatherLogs { get; init; } = true;

        public bool ShowLatLngLogs { get; init; } = true;
        public bool ShowMeshyLogs { get; init; } = true;

        public bool ShowHighDetailLogs { get; init; } = true;

        public bool ShowRendererDebugLogs { get; init; } = false;

        public bool ShowErrorLogs { get; init; } = true;

        public double GlbRotationXDegrees { get; init; } = 0;

        public double GlbRotationYDegrees { get; init; } = 0;

        public double GlbRotationZDegrees { get; init; } = 0;

        public double GlbScale { get; init; } = 1;

        public double GlbOffsetX { get; init; } = 0;

        public double GlbOffsetY { get; init; } = 0;

        public double GlbOffsetZ { get; init; } = 0;
    }
}










using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Malie.Infrastructure;
using Malie.Interop;
using Malie.Models;
using Malie.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Drawing = System.Drawing;

namespace Malie.Windows;

public partial class WallpaperWindow : Window
{
    private const string RendererVirtualHostName = "isometric-render.local";
    public const string WallpaperAssetsVirtualHostName = "wallpaper-assets.local";

    private static readonly JsonSerializerOptions PayloadSerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TaskCompletionSource _readyTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly DispatcherTimer _attachmentWatchdogTimer = new()
    {
        Interval = TimeSpan.FromSeconds(20)
    };
    private readonly DispatcherTimer _displaySettingsDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(650)
    };

    private WallpaperAttachResult? _lastAttachResult;
    private IntPtr _nativeHandle;
    private string? _rendererRootFolder;
    private string? _rendererHtmlPath;
    private Uri? _rendererSourceUri;
    private string? _lastPayloadBase64;
    private bool _isClosed;
    private bool _isAttachedToDesktop;
    private bool _isDisplaySettingsUpdateInProgress;
    private int _displaySettingsSignalCount;
    private string _targetMonitorDeviceName = string.Empty;

    public event EventHandler<string>? DebugMessage;

    public WallpaperAttachResult? LastAttachResult => _lastAttachResult;

    public WallpaperWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _attachmentWatchdogTimer.Tick += OnAttachmentWatchdogTick;
        _displaySettingsDebounceTimer.Tick += OnDisplaySettingsDebounceTick;
    }

    public Task WaitUntilReadyAsync()
    {
        return _readyTaskCompletionSource.Task;
    }

    public void SetTargetMonitorDeviceName(string? monitorDeviceName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetTargetMonitorDeviceName(monitorDeviceName));
            return;
        }

        var normalized = DesktopWallpaperHost.NormalizeMonitorDeviceName(monitorDeviceName);
        if (string.Equals(normalized, _targetMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _targetMonitorDeviceName = normalized;
        ApplyDesktopBounds();
        EmitDebug(
            string.IsNullOrWhiteSpace(_targetMonitorDeviceName)
                ? "Target monitor set to primary."
                : $"Target monitor set to '{_targetMonitorDeviceName}'.");
    }

    public Task PushSceneAsync(SceneRenderPayload payload)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => PushSceneAsync(payload)).Task.Unwrap();
        }

        return PushSceneCoreAsync(payload);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _nativeHandle = new WindowInteropHelper(this).Handle;
        ApplyDesktopBounds();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webViewUserDataRoot = AppBranding.GetWebView2UserDataRoot("wallpaper-renderer");
            Directory.CreateDirectory(webViewUserDataRoot);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewUserDataRoot);

            await RendererView.EnsureCoreWebView2Async(webViewEnvironment);
            RendererView.CoreWebView2.ProcessFailed += OnRendererProcessFailed;
            RendererView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            RendererView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            RendererView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            RendererView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            var appDataRoot = AppBranding.GetLocalAppDataRoot();
            var meshyCacheRoot = Path.Combine(appDataRoot, "cache", "meshy-poi");
            var wallpaperAssetsRoot = Path.Combine(appDataRoot, "cache", "wallpaper-assets");
            _rendererRootFolder = Path.Combine(AppContext.BaseDirectory, "www");
            _rendererHtmlPath = Path.Combine(_rendererRootFolder, "index.html");
            if (!File.Exists(_rendererHtmlPath))
            {
                throw new FileNotFoundException("Renderer file not found", _rendererHtmlPath);
            }

            RendererView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                RendererVirtualHostName,
                _rendererRootFolder,
                CoreWebView2HostResourceAccessKind.Allow);
            Directory.CreateDirectory(meshyCacheRoot);
            Directory.CreateDirectory(wallpaperAssetsRoot);
            RendererView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MeshySceneService.CacheVirtualHostName,
                meshyCacheRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            RendererView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                WallpaperAssetsVirtualHostName,
                wallpaperAssetsRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            RendererView.CoreWebView2.WebMessageReceived += (_, messageArgs) =>
            {
                try
                {
                    var json = messageArgs.WebMessageAsJson;
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        EmitDebug($"Renderer event: {json}");
                    }
                }
                catch (Exception ex)
                {
                    EmitDebug($"Renderer message parse failed: {ex.Message}");
                }
            };

            RendererView.NavigationCompleted += (_, navigationArgs) =>
            {
                if (navigationArgs.IsSuccess)
                {
                    EmitDebug("Renderer navigation completed successfully.");
                    _readyTaskCompletionSource.TrySetResult();
                    _ = Dispatcher.InvokeAsync(() => ReplayLastPayloadAsync("navigation completed"));
                }
                else
                {
                    EmitDebug($"Renderer navigation failed: {navigationArgs.WebErrorStatus}");
                    _readyTaskCompletionSource.TrySetException(
                        new InvalidOperationException($"Renderer failed to load: {navigationArgs.WebErrorStatus}"));
                }
            };

            _rendererSourceUri = new Uri($"https://{RendererVirtualHostName}/index.html");
            EmitDebug($"Renderer loading from: {_rendererSourceUri}");
            RendererView.Source = _rendererSourceUri;

            // Initialize WebView2 while top-level, then attach to desktop host.
            AttachToDesktopCore("Renderer initialized");

            // Re-apply bounds after WebView has been initialized/attached to ensure full-size host layout.
            _ = Dispatcher.BeginInvoke(new Action(ApplyDesktopBounds));
            _attachmentWatchdogTimer.Start();
        }
        catch (Exception ex)
        {
            EmitDebug($"Renderer initialization failed: {ex}");
            _readyTaskCompletionSource.TrySetException(ex);
        }
    }

    private async Task PushSceneCoreAsync(SceneRenderPayload payload)
    {
        await WaitUntilReadyAsync();

        var json = JsonSerializer.Serialize(payload, PayloadSerializationOptions);
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        _lastPayloadBase64 = base64Payload;
        await ExecutePayloadScriptAsync(base64Payload);

        var viewport = await RendererView.ExecuteScriptAsync(
            "(() => ({ w: window.innerWidth, h: window.innerHeight, dpr: window.devicePixelRatio, docH: document.documentElement.clientHeight, bodyH: document.body.clientHeight }))();");

        EmitDebug(
            $"Renderer payload applied: {payload.Weather.LocationName} | {payload.Weather.ShortForecast} | " +
            $"{payload.Scene.AccentEffect} | Seed {payload.Scene.Seed}");
        EmitDebug($"Renderer viewport metrics: {viewport}");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _attachmentWatchdogTimer.Stop();
        _attachmentWatchdogTimer.Tick -= OnAttachmentWatchdogTick;
        _displaySettingsDebounceTimer.Stop();
        _displaySettingsDebounceTimer.Tick -= OnDisplaySettingsDebounceTick;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnDisplaySettingsChanged(sender, e));
            return;
        }

        _displaySettingsSignalCount += 1;
        _displaySettingsDebounceTimer.Stop();
        _displaySettingsDebounceTimer.Start();
        EmitDebug($"Display settings signal received ({_displaySettingsSignalCount}); waiting for stable monitor state.");
    }

    private async void OnDisplaySettingsDebounceTick(object? sender, EventArgs e)
    {
        _displaySettingsDebounceTimer.Stop();

        if (_isClosed)
        {
            return;
        }

        if (_isDisplaySettingsUpdateInProgress)
        {
            _displaySettingsDebounceTimer.Start();
            return;
        }

        _isDisplaySettingsUpdateInProgress = true;
        var signalCount = _displaySettingsSignalCount;
        _displaySettingsSignalCount = 0;

        try
        {
            if (_isAttachedToDesktop)
            {
                AttachToDesktopCore("Display settings changed (debounced)");
            }

            ApplyDesktopBounds();
            await ReplayLastPayloadAsync("Display settings changed");

            var selectedBounds = DesktopWallpaperHost.GetScreenRectangle(_targetMonitorDeviceName);
            EmitDebug(
                $"Display settings applied after debounce ({signalCount} signal(s)). " +
                $"Target monitor px: {selectedBounds.Width}x{selectedBounds.Height} " +
                $"({(string.IsNullOrWhiteSpace(_targetMonitorDeviceName) ? "primary" : _targetMonitorDeviceName)}).");
        }
        catch (Exception ex)
        {
            EmitDebug($"Display settings recovery failed: {ex.Message}");
        }
        finally
        {
            _isDisplaySettingsUpdateInProgress = false;
            if (_displaySettingsSignalCount > 0)
            {
                _displaySettingsDebounceTimer.Start();
            }
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSessionSwitch(sender, e));
            return;
        }

        EmitDebug($"Session switch event: {e.Reason}");
        if (e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
        {
            _ = RecoverAfterSessionResumeAsync($"Session switch {e.Reason}", forceRendererReload: true);
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
            EmitDebug("Power resume detected.");
            _ = RecoverAfterSessionResumeAsync("Power resume", forceRendererReload: true);
        }
    }

    private void OnAttachmentWatchdogTick(object? sender, EventArgs e)
    {
        if (_isClosed || _nativeHandle == IntPtr.Zero)
        {
            return;
        }

        if (!_isAttachedToDesktop)
        {
            return;
        }

        var parentHandle = DesktopWallpaperHost.GetParentHandle(_nativeHandle);
        var parentClass = DesktopWallpaperHost.DescribeWindowClass(parentHandle);
        var parentIsValid = DesktopWallpaperHost.IsValidWindowHandle(parentHandle);
        var classLooksCorrect = string.Equals(parentClass, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parentClass, "Progman", StringComparison.OrdinalIgnoreCase);

        if (!parentIsValid || !classLooksCorrect)
        {
            EmitDebug(
                $"Attach watchdog triggered reattach. Parent {DesktopWallpaperHost.DescribeHandle(parentHandle)} ({parentClass}), valid={parentIsValid}.");
            _ = RecoverAfterSessionResumeAsync("Attachment watchdog", forceRendererReload: false);
        }
    }

    public Task RecoverAfterSessionResumeAsync(string reason, bool forceRendererReload)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => RecoverAfterSessionResumeAsync(reason, forceRendererReload)).Task.Unwrap();
        }

        return RecoverAfterSessionResumeCoreAsync(reason, forceRendererReload);
    }

    private async Task RecoverAfterSessionResumeCoreAsync(string reason, bool forceRendererReload)
    {
        if (_isClosed)
        {
            return;
        }

        await _recoveryGate.WaitAsync();
        try
        {
            EmitDebug($"Recovering wallpaper host ({reason}).");

            if (RendererView.CoreWebView2 is null)
            {
                EmitDebug("Renderer core missing during recovery; attempting renderer reload.");
                if (_rendererSourceUri is not null)
                {
                    RendererView.Source = _rendererSourceUri;
                }
                else if (!string.IsNullOrWhiteSpace(_rendererHtmlPath) && File.Exists(_rendererHtmlPath))
                {
                    RendererView.Source = new Uri(_rendererHtmlPath);
                }
                return;
            }

            AttachToDesktopCore(reason);
            ApplyDesktopBounds();

            if (forceRendererReload)
            {
                RendererView.CoreWebView2.Reload();
                EmitDebug("Renderer reload requested after recovery.");
            }
            else
            {
                await ReplayLastPayloadAsync(reason);
            }
        }
        catch (Exception ex)
        {
            EmitDebug($"Recovery failed ({reason}): {ex.Message}");
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private void OnRendererProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        EmitDebug($"Renderer process failed: {e.ProcessFailedKind}.");
        _ = Dispatcher.InvokeAsync(() => RecoverAfterSessionResumeAsync("WebView2 process failure", forceRendererReload: true));
    }

    private void AttachToDesktopCore(string reason)
    {
        if (_nativeHandle == IntPtr.Zero)
        {
            return;
        }

        _lastAttachResult = DesktopWallpaperHost.AttachWindowToDesktop(_nativeHandle);
        _isAttachedToDesktop = _lastAttachResult.IsAttached;
        EmitDebug(
            $"Desktop attach ({reason}): {_lastAttachResult.IsAttached} | Strategy: {_lastAttachResult.Strategy} | " +
            $"Target: {DesktopWallpaperHost.DescribeHandle(_lastAttachResult.TargetParentHandle)} ({_lastAttachResult.TargetParentClass}) | " +
            $"Actual: {DesktopWallpaperHost.DescribeHandle(_lastAttachResult.ActualParentHandle)} ({_lastAttachResult.ActualParentClass}) | " +
            $"LastError: {_lastAttachResult.LastError} | Primary px: {_lastAttachResult.PrimaryScreenWidth}x{_lastAttachResult.PrimaryScreenHeight}");
        EmitDebug($"Desktop attach message: {_lastAttachResult.Message}");
    }

    private async Task ExecutePayloadScriptAsync(string base64Payload)
    {
        if (string.IsNullOrWhiteSpace(base64Payload))
        {
            return;
        }

        var script = $"window.renderFromHost(JSON.parse(atob('{base64Payload}')));";
        await RendererView.ExecuteScriptAsync(script);
    }

    private async Task ReplayLastPayloadAsync(string reason)
    {
        if (string.IsNullOrWhiteSpace(_lastPayloadBase64))
        {
            return;
        }

        try
        {
            await ExecutePayloadScriptAsync(_lastPayloadBase64);
            EmitDebug($"Renderer payload replayed ({reason}).");
        }
        catch (Exception ex)
        {
            EmitDebug($"Renderer payload replay failed ({reason}): {ex.Message}");
        }
    }

    private void ApplyDesktopBounds()
    {
        var targetBounds = DesktopWallpaperHost.GetScreenRectangle(_targetMonitorDeviceName);
        if (_lastAttachResult is { IsAttached: false })
        {
            targetBounds = DesktopWallpaperHost.GetScreenWorkingAreaRectangle(_targetMonitorDeviceName);
            EmitDebug(
                $"Desktop attach not confirmed; using working-area bounds {targetBounds.Width}x{targetBounds.Height} to avoid taskbar overlap.");
        }

        ApplyWpfBoundsFromScreen(targetBounds);

        if (_nativeHandle != IntPtr.Zero)
        {
            DesktopWallpaperHost.ResizeWindowToBounds(_nativeHandle, targetBounds);
        }

        var nativeRect = DesktopWallpaperHost.GetWindowRectangle(_nativeHandle);
        EmitDebug(
            $"Applied bounds | WPF DIPs: {Width:0.##}x{Height:0.##} at {Left:0.##},{Top:0.##} | " +
            $"HWND px: {nativeRect.Width}x{nativeRect.Height} at {nativeRect.X},{nativeRect.Y}");
    }

    private void ApplyWpfBoundsFromScreen(Drawing.Rectangle screenBounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1d : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1d : dpi.DpiScaleY;

        Left = screenBounds.X / scaleX;
        Top = screenBounds.Y / scaleY;
        Width = Math.Max(1, screenBounds.Width / scaleX);
        Height = Math.Max(1, screenBounds.Height / scaleY);
    }

    private void EmitDebug(string message)
    {
        DebugMessage?.Invoke(this, message);
    }
}

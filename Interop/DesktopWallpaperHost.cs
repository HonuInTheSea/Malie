using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace Malie.Interop;

internal static class DesktopWallpaperHost
{
    private const uint SpawnWorkerMessage = 0x052C;
    private const int WindowStyleIndex = -16;
    private const int ExtendedWindowStyleIndex = -20;
    private const long WindowStyleChild = 0x40000000L;
    private const long WindowStylePopup = 0x80000000L;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    public readonly record struct DisplayMonitorInfo(
        string DeviceName,
        string Label,
        bool IsPrimary,
        int X,
        int Y,
        int Width,
        int Height);

    public static WallpaperAttachResult AttachWindowToDesktop(IntPtr windowHandle)
    {
        var primaryBounds = GetPrimaryScreenBounds();
        var progmanHandle = FindWindow("Progman", null);
        if (progmanHandle == IntPtr.Zero)
        {
            return new WallpaperAttachResult(
                false,
                "Find Progman",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                GetParent(windowHandle),
                string.Empty,
                GetWindowClassName(GetParent(windowHandle)),
                Marshal.GetLastWin32Error(),
                primaryBounds.X,
                primaryBounds.Y,
                primaryBounds.Width,
                primaryBounds.Height,
                "Progman handle was not found.");
        }

        _ = SendMessageTimeout(
            progmanHandle,
            SpawnWorkerMessage,
            new IntPtr(0xD),
            new IntPtr(0),
            SendMessageTimeoutFlags.SMTO_NORMAL,
            1000,
            out _);

        _ = SendMessageTimeout(
            progmanHandle,
            SpawnWorkerMessage,
            new IntPtr(0xD),
            new IntPtr(1),
            SendMessageTimeoutFlags.SMTO_NORMAL,
            1000,
            out _);

        ApplyWallpaperWindowStyles(windowHandle);

        var primaryWorkerHandle = FindDesktopWorkerWindow(progmanHandle);
        var parentCandidates = BuildParentCandidates(progmanHandle, primaryWorkerHandle);
        var attempts = new List<string>();
        var setParentError = 0;
        var targetParent = IntPtr.Zero;
        var actualParent = GetParent(windowHandle);
        var strategy = "No strategy";
        var isAttached = false;

        foreach (var candidate in parentCandidates)
        {
            if (candidate.Handle == IntPtr.Zero || !IsWindow(candidate.Handle))
            {
                attempts.Add($"{candidate.Strategy}: skipped invalid handle {DescribeHandle(candidate.Handle)}.");
                continue;
            }

            strategy = candidate.Strategy;
            targetParent = candidate.Handle;
            setParentError = SetParentWithDpiContext(windowHandle, candidate.Handle);
            actualParent = GetParent(windowHandle);
            isAttached = actualParent == candidate.Handle;

            attempts.Add(
                $"{candidate.Strategy}: target {DescribeHandle(candidate.Handle)} ({GetWindowClassName(candidate.Handle)}), " +
                $"actual {DescribeHandle(actualParent)} ({GetWindowClassName(actualParent)}), error {setParentError}.");

            ResizeWindowToPrimaryScreen(windowHandle, candidate.PlaceAtBottom);
            _ = ShowWindow(windowHandle, ShowWindowCommand.SW_SHOW);

            if (isAttached)
            {
                break;
            }
        }

        if (targetParent == IntPtr.Zero && parentCandidates.Count > 0)
        {
            targetParent = parentCandidates[^1].Handle;
        }

        var message = isAttached
            ? $"Attached using {strategy}. Attempts: {string.Join(" | ", attempts)}"
            : $"Attach failed. Attempts: {string.Join(" | ", attempts)}";

        return new WallpaperAttachResult(
            isAttached,
            strategy,
            progmanHandle,
            primaryWorkerHandle,
            targetParent,
            actualParent,
            GetWindowClassName(targetParent),
            GetWindowClassName(actualParent),
            setParentError,
            primaryBounds.X,
            primaryBounds.Y,
            primaryBounds.Width,
            primaryBounds.Height,
            message);
    }

    public static void ResizeWindowToPrimaryScreen(IntPtr windowHandle, bool placeAtBottom = true)
    {
        var bounds = GetPrimaryScreenBounds();
        ResizeWindowToBounds(
            windowHandle,
            new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            placeAtBottom);
    }

    public static void ResizeWindowToBounds(IntPtr windowHandle, Rectangle bounds, bool placeAtBottom = true)
    {
        var relativeX = bounds.X;
        var relativeY = bounds.Y;

        var parentHandle = GetParent(windowHandle);
        if (parentHandle != IntPtr.Zero && GetWindowRect(parentHandle, out var parentRect))
        {
            relativeX = bounds.X - parentRect.Left;
            relativeY = bounds.Y - parentRect.Top;
        }

        var insertAfter = placeAtBottom ? HwndBottom : HwndTop;
        _ = SetWindowPos(
            windowHandle,
            insertAfter,
            relativeX,
            relativeY,
            bounds.Width,
            bounds.Height,
            SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_SHOWWINDOW);
    }

    public static Rectangle GetPrimaryScreenRectangle()
    {
        var bounds = GetScreenBounds(string.Empty);
        return new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static Rectangle GetScreenRectangle(string? monitorDeviceName)
    {
        var bounds = GetScreenBounds(monitorDeviceName);
        return new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static Rectangle GetScreenWorkingAreaRectangle(string? monitorDeviceName)
    {
        var bounds = GetScreenWorkingArea(monitorDeviceName);
        return new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static string NormalizeMonitorDeviceName(string? monitorDeviceName)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            return string.Empty;
        }

        var candidate = monitorDeviceName.Trim();
        var matched = GetScreenByDeviceName(candidate);
        return matched is null ? string.Empty : matched.DeviceName;
    }

    public static IReadOnlyList<DisplayMonitorInfo> GetDisplayMonitors()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens is null || screens.Length == 0)
        {
            return Array.Empty<DisplayMonitorInfo>();
        }

        var monitorsByDeviceName = new Dictionary<string, DisplayMonitorInfo>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            var bounds = screen.Bounds;
            var deviceName = screen.DeviceName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                continue;
            }

            var monitorInfo = new DisplayMonitorInfo(
                deviceName,
                BuildMonitorLabel(screen, index),
                screen.Primary,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);

            if (!monitorsByDeviceName.TryGetValue(deviceName, out var existing))
            {
                monitorsByDeviceName[deviceName] = monitorInfo;
                continue;
            }

            if (!existing.IsPrimary && monitorInfo.IsPrimary)
            {
                monitorsByDeviceName[deviceName] = monitorInfo;
            }
        }

        return monitorsByDeviceName.Values.ToArray();
    }

    public static Rectangle GetWindowRectangle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return Rectangle.Empty;
        }

        if (!GetWindowRect(windowHandle, out var rect))
        {
            return Rectangle.Empty;
        }

        return new Rectangle(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    public static IntPtr GetParentHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return GetParent(windowHandle);
    }

    public static bool IsValidWindowHandle(IntPtr handle)
    {
        return handle != IntPtr.Zero && IsWindow(handle);
    }

    public static string DescribeWindowClass(IntPtr handle)
    {
        return GetWindowClassName(handle);
    }

    public static string DescribeHandle(IntPtr handle)
    {
        return $"0x{handle.ToInt64():X}";
    }

    private static List<ParentCandidate> BuildParentCandidates(IntPtr progmanHandle, IntPtr primaryWorkerHandle)
    {
        var candidates = new List<ParentCandidate>();
        var seen = new HashSet<long>();

        static void AddCandidate(List<ParentCandidate> target, HashSet<long> seenHandles, IntPtr handle, string strategy, bool placeAtBottom)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var numeric = handle.ToInt64();
            if (!seenHandles.Add(numeric))
            {
                return;
            }

            target.Add(new ParentCandidate(handle, strategy, placeAtBottom));
        }

        // Prefer the WorkerW sibling found from the SHELLDLL_DefView host because it is typically behind desktop icons.
        if (primaryWorkerHandle != IntPtr.Zero && !ContainsShellDefView(primaryWorkerHandle))
        {
            AddCandidate(candidates, seen, primaryWorkerHandle, "WorkerW sibling", true);
        }

        foreach (var workerHandle in EnumerateTopLevelWorkerWindows())
        {
            if (ContainsShellDefView(workerHandle))
            {
                continue;
            }

            AddCandidate(candidates, seen, workerHandle, "Top-level WorkerW scan", true);
        }

        AddCandidate(candidates, seen, FindProgmanChildWorkerWindow(progmanHandle), "Progman child WorkerW", true);
        AddCandidate(candidates, seen, progmanHandle, "Progman fallback", true);
        return candidates;
    }

    private static void ApplyWallpaperWindowStyles(IntPtr windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, WindowStyleIndex).ToInt64();
        style |= WindowStyleChild;
        style &= ~WindowStylePopup;
        _ = SetWindowLongPtr(windowHandle, WindowStyleIndex, new IntPtr(style));

        var extendedStyle = GetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex).ToInt64();
        extendedStyle |= ExtendedStyleTransparent;
        extendedStyle |= ExtendedStyleToolWindow;
        extendedStyle |= ExtendedStyleNoActivate;
        _ = SetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex, new IntPtr(extendedStyle));
    }

    private static IntPtr FindProgmanChildWorkerWindow(IntPtr progmanHandle)
    {
        if (progmanHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var workerHandle = FindWindowEx(progmanHandle, IntPtr.Zero, "WorkerW", null);
        while (workerHandle != IntPtr.Zero)
        {
            var containsShellDefView = FindWindowEx(workerHandle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            if (!containsShellDefView)
            {
                return workerHandle;
            }

            workerHandle = FindWindowEx(progmanHandle, workerHandle, "WorkerW", null);
        }

        return IntPtr.Zero;
    }

    private static bool ContainsShellDefView(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return FindWindowEx(windowHandle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
    }

    private static IntPtr FindDesktopWorkerWindow(IntPtr progmanHandle)
    {
        var shellDefViewHost = IntPtr.Zero;

        _ = EnumWindows(
            (topWindowHandle, _) =>
            {
                var shellDllDefViewHandle = FindWindowEx(topWindowHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellDllDefViewHandle == IntPtr.Zero)
                {
                    return true;
                }

                shellDefViewHost = topWindowHandle;
                return false;
            },
            IntPtr.Zero);

        if (shellDefViewHost != IntPtr.Zero)
        {
            var siblingWorker = FindWindowEx(IntPtr.Zero, shellDefViewHost, "WorkerW", null);
            if (siblingWorker != IntPtr.Zero)
            {
                return siblingWorker;
            }
        }

        if (progmanHandle != IntPtr.Zero)
        {
            var childWorker = FindWindowEx(progmanHandle, IntPtr.Zero, "WorkerW", null);
            if (childWorker != IntPtr.Zero)
            {
                return childWorker;
            }
        }

        var workerWindowHandle = FindWindow("WorkerW", null);
        while (workerWindowHandle != IntPtr.Zero)
        {
            var containsShellDefView = FindWindowEx(workerWindowHandle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            if (!containsShellDefView)
            {
                return workerWindowHandle;
            }

            workerWindowHandle = FindWindowEx(IntPtr.Zero, workerWindowHandle, "WorkerW", null);
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<IntPtr> EnumerateTopLevelWorkerWindows()
    {
        var workers = new List<IntPtr>();
        _ = EnumWindows(
            (windowHandle, _) =>
            {
                if (string.Equals(GetWindowClassName(windowHandle), "WorkerW", StringComparison.Ordinal))
                {
                    workers.Add(windowHandle);
                }

                return true;
            },
            IntPtr.Zero);

        return workers;
    }

    private static ScreenBounds GetPrimaryScreenBounds()
    {
        var primaryScreen = Forms.Screen.PrimaryScreen;
        if (primaryScreen is null)
        {
            return new ScreenBounds(0, 0, 1920, 1080);
        }

        var bounds = primaryScreen.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new ScreenBounds(0, 0, 1920, 1080);
        }

        return new ScreenBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static ScreenBounds GetScreenBounds(string? monitorDeviceName)
    {
        var screen = GetScreenByDeviceName(monitorDeviceName) ?? Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return new ScreenBounds(0, 0, 1920, 1080);
        }

        var bounds = screen.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return GetPrimaryScreenBounds();
        }

        return new ScreenBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static ScreenBounds GetScreenWorkingArea(string? monitorDeviceName)
    {
        var screen = GetScreenByDeviceName(monitorDeviceName) ?? Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return new ScreenBounds(0, 0, 1920, 1080);
        }

        var workingArea = screen.WorkingArea;
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return GetScreenBounds(monitorDeviceName);
        }

        return new ScreenBounds(workingArea.X, workingArea.Y, workingArea.Width, workingArea.Height);
    }

    private static Forms.Screen? GetScreenByDeviceName(string? monitorDeviceName)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            return null;
        }

        var candidate = monitorDeviceName.Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        foreach (var screen in Forms.Screen.AllScreens)
        {
            if (string.Equals(screen.DeviceName, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }

    private static string BuildMonitorLabel(Forms.Screen screen, int index)
    {
        var bounds = screen.Bounds;
        var label = $"Monitor {index + 1}";
        if (screen.Primary)
        {
            label += " (Primary)";
        }

        return $"{label} - {bounds.Width}x{bounds.Height}";
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return "None";
        }

        var builder = new StringBuilder(256);
        var copiedLength = GetClassName(handle, builder, builder.Capacity);
        return copiedLength <= 0 ? "Unknown" : builder.ToString();
    }

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, newValue)
            : new IntPtr(SetWindowLong32(windowHandle, index, newValue.ToInt32()));
    }

    private static int SetParentWithDpiContext(IntPtr childWindowHandle, IntPtr newParentHandle)
    {
        IntPtr? previousContext = null;
        try
        {
            var targetContext = GetWindowDpiAwarenessContext(newParentHandle);
            if (targetContext != IntPtr.Zero)
            {
                previousContext = SetThreadDpiAwarenessContext(targetContext);
            }

            SetLastError(0);
            _ = SetParent(childWindowHandle, newParentHandle);
            return Marshal.GetLastWin32Error();
        }
        finally
        {
            if (previousContext.HasValue && previousContext.Value != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(previousContext.Value);
            }
        }
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0000
    }

    [Flags]
    private enum SetWindowPosFlags : uint
    {
        SWP_NOACTIVATE = 0x0010,
        SWP_SHOWWINDOW = 0x0040
    }

    private enum ShowWindowCommand
    {
        SW_SHOW = 5
    }

    private readonly record struct ScreenBounds(int X, int Y, int Width, int Height);
    private readonly record struct ParentCandidate(IntPtr Handle, string Strategy, bool PlaceAtBottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr childHandle, IntPtr newParentHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        SetWindowPosFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        SendMessageTimeoutFlags flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr windowHandle, ShowWindowCommand command);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}

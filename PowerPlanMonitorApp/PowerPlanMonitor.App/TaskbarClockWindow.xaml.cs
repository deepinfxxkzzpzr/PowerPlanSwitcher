using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PowerPlanMonitor.App;

public partial class TaskbarClockWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _positionTimer;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    // 委托保存在字段里，防止被 GC 回收导致钩子回调崩溃
    private readonly WinEventDelegate _foregroundChangedCallback;
    private IntPtr _foregroundHook;
    private bool _hiddenForFullscreen;

    public TaskbarClockWindow()
    {
        InitializeComponent();
        SetTextColor();
        _foregroundChangedCallback = OnForegroundChanged;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _positionTimer.Tick += (_, _) => RefreshOverlay();
        Deactivated += (_, _) => KeepAboveTaskbar();
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _positionTimer.Stop();
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
        };
    }

    public void ApplyTimeZone(string timeZoneId)
    {
        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            _timeZone = TryGetChinaTimeZone();
        }
        catch (InvalidTimeZoneException)
        {
            _timeZone = TimeZoneInfo.Utc;
        }

        UpdateClock();
    }

    private static TimeZoneInfo TryGetChinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch
        {
            return TimeZoneInfo.CreateCustomTimeZone("UTC+08:00", TimeSpan.FromHours(8), "(UTC+08:00)", "UTC+08:00");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _foregroundChangedCallback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        RefreshOverlay();
        UpdateClock();
        _clockTimer.Start();
        _positionTimer.Start();
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint timestamp)
    {
        // 回调来自系统线程消息循环，切回 UI 线程处理
        Dispatcher.BeginInvoke(RefreshOverlay, DispatcherPriority.Background);
    }

    private void RefreshOverlay()
    {
        if (IsFullscreenAppOnSameMonitor())
        {
            if (!_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                Visibility = Visibility.Hidden;
            }

            return;
        }

        if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Visibility = Visibility.Visible;
        }

        PositionBesideSystemTray();
    }

    private bool IsFullscreenAppOnSameMonitor()
    {
        var foreground = GetForegroundWindow();
        var self = new WindowInteropHelper(this).Handle;
        if (foreground == IntPtr.Zero || foreground == self)
        {
            return false;
        }

        // 桌面、任务栏、开始菜单等外壳窗口即使铺满屏幕也不算全屏应用
        var className = GetClassNameOf(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow")
        {
            return false;
        }

        if (!GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (self == IntPtr.Zero || monitor != MonitorFromWindow(self, MonitorDefaultToNearest))
        {
            return false;
        }

        var info = new MonitorInfoNative { Size = Marshal.SizeOf<MonitorInfoNative>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return rect.Left <= info.Monitor.Left
            && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right
            && rect.Bottom >= info.Monitor.Bottom;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private void UpdateClock()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
        TimeText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString("yyyy/M/d");
    }

    private void PositionBesideSystemTray()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var anchorRect = taskbarRect;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect))
        {
            anchorRect = trayRect;
        }

        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var taskbarTopLeft = fromDevice.Transform(new System.Windows.Point(taskbarRect.Left, taskbarRect.Top));
        var taskbarBottomRight = fromDevice.Transform(new System.Windows.Point(taskbarRect.Right, taskbarRect.Bottom));
        var anchorTopLeft = fromDevice.Transform(new System.Windows.Point(anchorRect.Left, anchorRect.Top));

        var horizontal = taskbarRect.Right - taskbarRect.Left >= taskbarRect.Bottom - taskbarRect.Top;
        if (horizontal)
        {
            Height = Math.Max(32, taskbarBottomRight.Y - taskbarTopLeft.Y);
            Left = Math.Max(taskbarTopLeft.X, anchorTopLeft.X - Width - 2);
            Top = taskbarTopLeft.Y;
        }
        else
        {
            Width = Math.Max(48, taskbarBottomRight.X - taskbarTopLeft.X);
            Left = taskbarTopLeft.X;
            Top = Math.Max(taskbarTopLeft.Y, anchorTopLeft.Y - Height - 2);
        }

        KeepAboveTaskbar();
    }

    private void KeepAboveTaskbar()
    {
        if (_hiddenForFullscreen)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpShowWindow | 0x0001 | 0x0002);
        }
    }

    private void SetTextColor()
    {
        var lightTaskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
        }

        var brush = lightTaskbar ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        TimeText.Foreground = brush;
        DateText.Foreground = brush;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    private const uint MonitorDefaultToNearest = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoNative info);

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint timestamp);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoNative
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }
}

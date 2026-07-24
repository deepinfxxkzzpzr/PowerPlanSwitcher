using System.Runtime.InteropServices;
using System.Windows.Interop;
using PowerPlanMonitor.App.Models;

namespace PowerPlanMonitor.App.Services;

public sealed class HotkeyService
{
    private const int HotkeyId = 0x5050;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private IntPtr _handle;

    public event EventHandler? Pressed;

    public bool Register(IntPtr handle, AppConfig config)
    {
        Unregister();
        if (!config.HotkeyEnabled)
        {
            return true;
        }

        _handle = handle;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        if (RegisterHotKey(_handle, HotkeyId, BuildModifier(config), BuildVirtualKey(config.HotkeyKey)))
        {
            return true;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
        return false;
    }

    public void Unregister()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
            _handle = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint BuildModifier(AppConfig config)
    {
        uint modifier = 0;
        if (config.HotkeyAlt) modifier |= 0x0001;
        if (config.HotkeyCtrl) modifier |= 0x0002;
        if (config.HotkeyShift) modifier |= 0x0004;
        if (config.HotkeyWin) modifier |= 0x0008;
        return modifier;
    }

    private static uint BuildVirtualKey(string key)
    {
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1)
        {
            return key[0];
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var index) && index is >= 1 and <= 12)
        {
            return (uint)(0x6F + index);
        }

        return 'P';
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

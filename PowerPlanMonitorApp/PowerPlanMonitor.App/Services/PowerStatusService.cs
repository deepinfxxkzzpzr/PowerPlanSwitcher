using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PowerPlanMonitor.App.Services;

public sealed class PowerStatusService : IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0x00000000;
    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    private HwndSource? _source;
    private IntPtr _notificationHandle;

    public event EventHandler<bool>? PowerSourceChanged;
    public event EventHandler? SystemResumed;

    public bool Initialize(IntPtr windowHandle)
    {
        Dispose();
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        _source = HwndSource.FromHwnd(windowHandle);
        if (_source is null)
        {
            return false;
        }

        _source.AddHook(WndProc);
        _notificationHandle = RegisterPowerSettingNotification(windowHandle, in AcDcPowerSource, DeviceNotifyWindowHandle);
        if (_notificationHandle != IntPtr.Zero)
        {
            return true;
        }

        _source.RemoveHook(WndProc);
        _source = null;
        return false;
    }

    public int GetAcLineStatus()
    {
        return GetSystemPowerStatus(out var status) ? status.ACLineStatus : -1;
    }

    public void Dispose()
    {
        if (_notificationHandle != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmPowerBroadcast)
        {
            return IntPtr.Zero;
        }

        try
        {
            var eventCode = wParam.ToInt32();
            if (eventCode == PbtPowerSettingChange && TryReadPowerSource(lParam, out var pluggedIn))
            {
                NotifyPowerSourceChanged(pluggedIn);
            }
            else if (eventCode == PbtApmResumeAutomatic)
            {
                NotifySystemResumed();
            }
        }
        catch
        {
            // A system-message callback must never let an event handler terminate the tray process.
        }

        return IntPtr.Zero;
    }

    private static bool TryReadPowerSource(IntPtr lParam, out bool pluggedIn)
    {
        pluggedIn = false;
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        var setting = Marshal.PtrToStructure<PowerBroadcastSettingHeader>(lParam);
        if (setting.PowerSetting != AcDcPowerSource || setting.DataLength < sizeof(int))
        {
            return false;
        }

        var value = Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSettingHeader>());
        if (value is not (0 or 1))
        {
            return false;
        }

        pluggedIn = value == 0;
        return true;
    }

    private void NotifyPowerSourceChanged(bool pluggedIn)
    {
        foreach (EventHandler<bool> handler in PowerSourceChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, pluggedIn);
            }
            catch
            {
                // One subscriber cannot be allowed to end system event delivery for the tray process.
            }
        }
    }

    private void NotifySystemResumed()
    {
        foreach (EventHandler handler in SystemResumed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // One subscriber cannot be allowed to end system event delivery for the tray process.
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        in Guid powerSettingGuid,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSettingHeader
    {
        public Guid PowerSetting;
        public uint DataLength;
    }
}

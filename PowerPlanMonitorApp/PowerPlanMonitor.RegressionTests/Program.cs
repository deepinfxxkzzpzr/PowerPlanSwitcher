using System.Runtime.InteropServices;
using System.Windows.Interop;
using PowerPlanMonitor.App.Services;

namespace PowerPlanMonitor.RegressionTests;

internal static class Program
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    [STAThread]
    private static int Main()
    {
        try
        {
            using var source = new HwndSource(new HwndSourceParameters("PowerPlanMonitor.RegressionTests")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            });
            using var service = new PowerStatusService();
            Assert(service.Initialize(source.Handle), "Power-status notification initialization failed.");

            var reportedStates = new List<bool>();
            service.PowerSourceChanged += (_, pluggedIn) => reportedStates.Add(pluggedIn);

            SendPowerSourceChange(source.Handle, 0);
            SendPowerSourceChange(source.Handle, 1);
            Assert(reportedStates.SequenceEqual([true, false]), "AC/DC notifications were not mapped to the expected power states.");

            service.PowerSourceChanged += (_, _) => throw new InvalidOperationException("Simulated switch failure");
            SendPowerSourceChange(source.Handle, 0);

            Console.WriteLine("PASS: power source changes are delivered and a failing subscriber cannot escape the native message callback.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
    }

    private static void SendPowerSourceChange(IntPtr handle, int value)
    {
        var headerSize = Marshal.SizeOf<PowerBroadcastSettingHeader>();
        var buffer = Marshal.AllocHGlobal(headerSize + sizeof(int));
        try
        {
            Marshal.StructureToPtr(new PowerBroadcastSettingHeader(AcDcPowerSource, sizeof(int)), buffer, false);
            Marshal.WriteInt32(buffer, headerSize, value);
            _ = SendMessage(handle, WmPowerBroadcast, (IntPtr)PbtPowerSettingChange, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PowerBroadcastSettingHeader(Guid PowerSetting, uint DataLength);
}

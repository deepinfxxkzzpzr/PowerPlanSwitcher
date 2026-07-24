using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32;

namespace PowerPlanMonitor.Diagnostics;

internal static class Program
{
    public static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("PowerPlanMonitor Hardware Diagnostics");
        Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"Elevated: {IsElevated()}");
        Console.WriteLine();

        var pawnIo = GetPawnIoStatus();
        Console.WriteLine("PawnIO");
        Console.WriteLine($"  Installed: {pawnIo.Installed}");
        Console.WriteLine($"  Version: {pawnIo.Version?.ToString() ?? "-"}");
        Console.WriteLine($"  DeviceOpen: {pawnIo.DeviceOpen}");
        Console.WriteLine();

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPowerMonitorEnabled = false
        };

        try
        {
            computer.Open();
            computer.Accept(new UpdateVisitor());
            computer.Accept(new UpdateVisitor());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LibreHardwareMonitor.Open failed: {ex}");
        }

        Console.WriteLine("LibreHardwareMonitor Sensors");
        foreach (var hardware in computer.Hardware)
        {
            PrintHardware(hardware, "  ");
        }

        Console.WriteLine();
        PrintWindowsProcessorPowerFrequency();
        Console.WriteLine();

        PrintCpuClockSummary(computer);
        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine("CPU temperature candidates");
        var candidates = computer.Hardware
            .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
            .SelectMany(EnumerateSensors)
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .ToArray();

        if (candidates.Length == 0)
        {
            Console.WriteLine("  No CPU temperature sensors were created.");
        }
        else
        {
            foreach (var sensor in candidates)
            {
                Console.WriteLine($"  {sensor.Name}: {FormatValue(sensor)} [{sensor.Identifier}]");
            }
        }

        Console.WriteLine();
        PrintDirectIntelMsr();
        Console.WriteLine();

        Console.WriteLine("Conclusion");
        if (!pawnIo.Installed || !pawnIo.DeviceOpen)
        {
            Console.WriteLine("  PawnIO is not ready. Intel CPU MSR temperature reads will fail until the driver is installed and accessible.");
        }
        else if (candidates.Any(sensor => sensor.Value is > 0 and < 120))
        {
            Console.WriteLine("  CPU temperature is readable through LibreHardwareMonitor.");
        }
        else
        {
            Console.WriteLine("  PawnIO is ready but CPU temperature values are still empty. Next target: CPU model support / MSR valid-bit behavior.");
        }

        computer.Close();
    }

    private static void PrintDirectIntelMsr()
    {
        Console.WriteLine("Direct Intel MSR temperature");
        try
        {
            using var reader = new DirectIntelMsrReader();
            var snapshot = reader.ReadSnapshot();
            if (snapshot.Error is not null)
            {
                Console.WriteLine($"  Error: {snapshot.Error}");
                return;
            }

            Console.WriteLine($"  IA32_TEMPERATURE_TARGET 0x01A2: read={snapshot.TargetRead}, raw=0x{snapshot.TargetRaw:X16}, tjMax={FormatNullable(snapshot.TjMax)} °C");
            Console.WriteLine($"  IA32_PACKAGE_THERM_STATUS 0x01B1: read={snapshot.PackageRead}, raw=0x{snapshot.PackageRaw:X16}, temp={FormatNullable(snapshot.PackageTemperature)} °C");
            Console.WriteLine($"  IA32_THERM_STATUS 0x019C: read={snapshot.CoreRead}, raw=0x{snapshot.CoreRaw:X16}, temp={FormatNullable(snapshot.CoreTemperature)} °C");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.Message}");
        }
    }

    private static void PrintCpuClockSummary(Computer computer)
    {
        Console.WriteLine("CPU clock candidates");
        var clocks = computer.Hardware
            .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
            .SelectMany(EnumerateSensors)
            .Where(sensor => sensor.SensorType == SensorType.Clock)
            .OrderBy(sensor => sensor.Index)
            .ToArray();

        if (clocks.Length == 0)
        {
            Console.WriteLine("  No CPU clock sensors were created.");
            return;
        }

        foreach (var sensor in clocks)
        {
            Console.WriteLine($"  {sensor.Name}: {FormatValue(sensor)} [{sensor.Identifier}]");
        }

        var preferredSensor = SelectPreferredCpuClockSensor(clocks);
        if (preferredSensor is null)
        {
            Console.WriteLine("  Preferred display sensor: null");
            return;
        }

        Console.WriteLine($"  Preferred display sensor: {preferredSensor.Name} [{preferredSensor.Identifier}]");
        Console.WriteLine($"  Preferred display value: {FormatValue(preferredSensor)}");

        var coreClocks = clocks
            .Where(sensor => sensor.Value is > 100 and < 8000)
            .Where(sensor => IsCoreClockSensor(sensor.Name))
            .Select(sensor => sensor.Value!.Value)
            .ToArray();

        if (coreClocks.Length > 0)
        {
            Console.WriteLine($"  Max among valid cores: {FormatFrequency(coreClocks.Max())}");
            Console.WriteLine($"  Average among valid cores: {FormatFrequency(coreClocks.Average())}");
            Console.WriteLine($"  Min among valid cores: {FormatFrequency(coreClocks.Min())}");
        }
    }

    private static void PrintWindowsProcessorPowerFrequency()
    {
        Console.WriteLine("Windows ProcessorInformation frequency");
        try
        {
            var values = ReadWindowsCurrentMhz();
            if (values.Length == 0)
            {
                Console.WriteLine("  No valid CurrentMhz values.");
                return;
            }

            Console.WriteLine($"  Logical processors: {values.Length}");
            Console.WriteLine($"  Preferred display value: {FormatFrequency(values[0])}");
            Console.WriteLine($"  Average: {FormatFrequency(values.Average())}");
            Console.WriteLine($"  Min: {FormatFrequency(values.Min())}");
            Console.WriteLine($"  Max: {FormatFrequency(values.Max())}");
            Console.WriteLine($"  Values: {string.Join(", ", values.Select(value => value.ToString("0")))} MHz");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.Message}");
        }
    }

    private static double[] ReadWindowsCurrentMhz()
    {
        var processorCount = Environment.ProcessorCount;
        var itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
        var size = itemSize * processorCount;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = CallNtPowerInformation(11, IntPtr.Zero, 0, buffer, (uint)size);
            if (status != 0)
            {
                return [];
            }

            var values = new List<double>(processorCount);
            for (var index = 0; index < processorCount; index++)
            {
                var item = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer + (index * itemSize));
                if (item.CurrentMhz is > 100 and < 8000)
                {
                    values.Add(item.CurrentMhz);
                }
            }

            return values.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void PrintHardware(IHardware hardware, string indent)
    {
        Console.WriteLine($"{indent}{hardware.HardwareType}: {hardware.Name} [{hardware.Identifier}]");
        hardware.Update();

        foreach (var sensor in hardware.Sensors.OrderBy(sensor => sensor.SensorType).ThenBy(sensor => sensor.Index))
        {
            Console.WriteLine($"{indent}  {sensor.SensorType,-12} {sensor.Name,-28} {FormatValue(sensor),8} [{sensor.Identifier}]");
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            PrintHardware(subHardware, indent + "  ");
        }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            yield return sensor;
        }

        foreach (var child in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensors(child))
            {
                yield return sensor;
            }
        }
    }

    private static string FormatValue(ISensor sensor)
    {
        if (sensor.Value is null)
        {
            return "null";
        }

        var suffix = sensor.SensorType switch
        {
            SensorType.Temperature => " °C",
            SensorType.Load => " %",
            SensorType.Clock => " MHz",
            SensorType.Voltage => " V",
            SensorType.Power => " W",
            _ => ""
        };
        return $"{sensor.Value:0.##}{suffix}";
    }

    private static string FormatNullable(double? value)
        => value is null ? "null" : $"{value:0.#}";

    private static bool IsCoreClockSensor(string name)
    {
        return name.Contains("core", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("bus", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("ring", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("uncore", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("memory", StringComparison.OrdinalIgnoreCase);
    }

    private static ISensor? SelectPreferredCpuClockSensor(IEnumerable<ISensor> sensors)
    {
        return sensors
            .Where(sensor => IsCoreClockSensor(sensor.Name))
            .Select(sensor => new
            {
                Sensor = sensor,
                Priority = GetCpuClockSensorPriority(sensor.Name)
            })
            .Where(candidate => candidate.Priority is not null)
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Sensor.Index)
            .Select(candidate => candidate.Sensor)
            .FirstOrDefault();
    }

    private static int? GetCpuClockSensorPriority(string name)
    {
        if (TryGetCoreNumber(name, "p-core", out var performanceCoreNumber))
        {
            return performanceCoreNumber;
        }

        if (TryGetCoreNumber(name, "core", out var genericCoreNumber))
        {
            return 100 + genericCoreNumber;
        }

        return name.Contains("core", StringComparison.OrdinalIgnoreCase) ? 10_000 : null;
    }

    private static bool TryGetCoreNumber(string name, string marker, out int normalizedNumber)
    {
        var markerIndex = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            normalizedNumber = 0;
            return false;
        }

        for (var index = markerIndex + marker.Length; index < name.Length; index++)
        {
            if (!char.IsDigit(name[index]))
            {
                continue;
            }

            var end = index + 1;
            while (end < name.Length && char.IsDigit(name[end]))
            {
                end++;
            }

            if (int.TryParse(name[index..end], out var rawNumber))
            {
                normalizedNumber = rawNumber > 0 ? rawNumber - 1 : 0;
                return true;
            }

            break;
        }

        normalizedNumber = 0;
        return false;
    }

    private static string FormatFrequency(double mhz)
        => mhz >= 1000 ? $"{mhz / 1000:0.00}GHz" : $"{mhz:0}MHz";

    private static (bool Installed, Version? Version, bool DeviceOpen) GetPawnIoStatus()
    {
        var version = ReadPawnIoVersion(RegistryView.Registry64) ?? ReadPawnIoVersion(RegistryView.Registry32);
        return (version is not null, version, CanOpenPawnIo());
    }

    private static Version? ReadPawnIoVersion(RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CanOpenPawnIo()
    {
        var handle = CreateFile(
            @"\\?\GLOBALROOT\Device\PawnIO",
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            FileAttributes.Normal,
            IntPtr.Zero);

        if (handle == new IntPtr(-1))
        {
            return false;
        }

        CloseHandle(handle);
        return true;
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        FileAccess dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private sealed class DirectIntelMsrReader : IDisposable
    {
        private const uint Ia32ThermStatusMsr = 0x019C;
        private const uint Ia32TemperatureTarget = 0x01A2;
        private const uint Ia32PackageThermStatus = 0x01B1;

        private readonly IntelMsr _intelMsr = new();

        public DirectIntelMsrSnapshot ReadSnapshot()
        {
            try
            {
                var target = ReadRaw(Ia32TemperatureTarget);
                var packageStatus = ReadRaw(Ia32PackageThermStatus);
                var coreStatus = ReadRaw(Ia32ThermStatusMsr);
                var tjMax = DecodeTjMax(target);

                return new DirectIntelMsrSnapshot(
                    target.Success,
                    target.Value,
                    packageStatus.Success,
                    packageStatus.Value,
                    DecodeTemperature(packageStatus, tjMax),
                    coreStatus.Success,
                    coreStatus.Value,
                    DecodeTemperature(coreStatus, tjMax),
                    tjMax);
            }
            catch (Exception ex)
            {
                return DirectIntelMsrSnapshot.Failed(ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _intelMsr.Close();
            }
            catch
            {
            }
        }

        private RawMsrRead ReadRaw(uint msr)
        {
            var success = _intelMsr.ReadMsr(msr, out ulong value);
            return new RawMsrRead(success, value);
        }

        private static double? DecodeTjMax(RawMsrRead raw)
        {
            if (!raw.Success)
            {
                return null;
            }

            var tjMax = (raw.Value >> 16) & 0xFF;
            return tjMax is > 0 and < 140 ? tjMax : null;
        }

        private static double? DecodeTemperature(RawMsrRead raw, double? tjMax)
        {
            if (!raw.Success || tjMax is null || (raw.Value & 0x80000000) == 0)
            {
                return null;
            }

            var distanceToTjMax = (raw.Value >> 16) & 0x7F;
            var temperature = tjMax.Value - distanceToTjMax;
            return temperature is > 0 and < 120 ? temperature : null;
        }

        private readonly record struct RawMsrRead(bool Success, ulong Value);
    }

    private sealed record DirectIntelMsrSnapshot(
        bool TargetRead,
        ulong TargetRaw,
        bool PackageRead,
        ulong PackageRaw,
        double? PackageTemperature,
        bool CoreRead,
        ulong CoreRaw,
        double? CoreTemperature,
        double? TjMax,
        string? Error = null)
    {
        public static DirectIntelMsrSnapshot Failed(string error)
            => new(false, 0, false, 0, null, false, 0, null, null, error);
    }
}

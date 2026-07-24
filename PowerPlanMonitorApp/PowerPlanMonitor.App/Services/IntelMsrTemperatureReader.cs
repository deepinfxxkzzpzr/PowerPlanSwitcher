using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace PowerPlanMonitor.App.Services;

public sealed class IntelMsrTemperatureReader : IDisposable
{
    private const uint Ia32ThermStatusMsr = 0x019C;
    private const uint Ia32TemperatureTarget = 0x01A2;
    private const uint Ia32PackageThermStatus = 0x01B1;
    private const uint Ia32PerfStatus = 0x0198;
    private const uint MsrPlatformInfo = 0x00CE;

    private readonly IntelMsr _intelMsr = new();
    private double? _busClockMhz;

    public double? ReadAverageCoreFrequency()
    {
        try
        {
            _busClockMhz ??= ReadBusClockMhz();
            if (_busClockMhz is null)
            {
                return null;
            }

            double total = 0;
            var count = 0;
            for (var index = 0; index < Environment.ProcessorCount; index++)
            {
                if (!_intelMsr.ReadMsr(Ia32PerfStatus, out uint eax, out _, GroupAffinity.Single(0, index)))
                {
                    continue;
                }

                var ratio = (eax >> 8) & 0xFF;
                var frequency = ratio * _busClockMhz.Value;
                if (ratio is > 0 and < 100 && frequency is > 100 and < 8000)
                {
                    total += frequency;
                    count++;
                }
            }

            return count > 0 ? total / count : null;
        }
        catch
        {
            return null;
        }
    }

    public double? ReadPackageTemperature()
    {
        try
        {
            var tjMax = ReadTjMax();
            if (tjMax is null)
            {
                return null;
            }

            return ReadTemperature(Ia32PackageThermStatus, tjMax.Value)
                   ?? ReadTemperature(Ia32ThermStatusMsr, tjMax.Value);
        }
        catch
        {
            return null;
        }
    }

    public IntelMsrTemperatureSnapshot ReadSnapshot()
    {
        try
        {
            var target = ReadRaw(Ia32TemperatureTarget);
            var packageStatus = ReadRaw(Ia32PackageThermStatus);
            var coreStatus = ReadRaw(Ia32ThermStatusMsr);
            var tjMax = DecodeTjMax(target);

            return new IntelMsrTemperatureSnapshot(
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
            return IntelMsrTemperatureSnapshot.Failed(ex.Message);
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

    private double? ReadTjMax()
    {
        var raw = ReadRaw(Ia32TemperatureTarget);
        return DecodeTjMax(raw);
    }

    private double? ReadTemperature(uint msr, double tjMax)
    {
        var raw = ReadRaw(msr);
        return DecodeTemperature(raw, tjMax);
    }

    private RawMsrRead ReadRaw(uint msr)
    {
        var success = _intelMsr.ReadMsr(msr, out ulong value);
        return new RawMsrRead(success, value);
    }

    private double? ReadBusClockMhz()
    {
        if (!_intelMsr.ReadMsr(MsrPlatformInfo, out ulong platformInfo))
        {
            return null;
        }

        var maximumNonTurboRatio = (platformInfo >> 8) & 0xFF;
        var maximumMhz = ReadWindowsMaximumMhz();
        if (maximumNonTurboRatio is 0 or > 100 || maximumMhz is null)
        {
            return null;
        }

        var busClock = maximumMhz.Value / maximumNonTurboRatio;
        return busClock is > 50 and < 200 ? busClock : null;
    }

    private static double? ReadWindowsMaximumMhz()
    {
        var itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
        var size = itemSize * Environment.ProcessorCount;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buffer, (uint)size) != 0)
            {
                return null;
            }

            uint maximum = 0;
            for (var index = 0; index < Environment.ProcessorCount; index++)
            {
                var item = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer + (index * itemSize));
                maximum = Math.Max(maximum, item.MaxMhz);
            }

            return maximum is > 100 and < 8000 ? maximum : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("powrprof.dll")]
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

public sealed record IntelMsrTemperatureSnapshot(
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
    public static IntelMsrTemperatureSnapshot Failed(string error)
        => new(false, 0, false, 0, null, false, 0, null, null, error);
}

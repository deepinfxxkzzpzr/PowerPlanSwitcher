using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using PowerPlanMonitor.App.Models;

namespace PowerPlanMonitor.App.Services;

public sealed class MetricsService : IDisposable
{
    private const double UnknownAdapterCapacityBytesPerSecond = 10_000_000_000d / 8d;
    private static readonly TimeSpan FrequencyRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TemperatureRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SensorRetryInterval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly PdhCpuUsageReader _pdhCpuUsageReader = new();
    private readonly object _networkLock = new();

    private Computer? _computer;
    private IntelMsrTemperatureReader? _intelMsrTemperatureReader;
    private NetworkInterface[] _networkAdapters = [];
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private long _lastSent;
    private long _lastReceived;
    private DateTime _lastNetworkAt = DateTime.UtcNow;
    private string _cachedFrequency = "--GHz";
    private readonly Queue<double> _frequencySamples = new();
    private string _cachedTemperature = "--°C";
    private DateTime _lastFrequencyRefresh = DateTime.MinValue;
    private DateTime _lastTemperatureRefresh = DateTime.MinValue;
    private DateTime _temperatureRetryAfter = DateTime.MinValue;
    private DateTime _libreTemperatureRetryAfter = DateTime.MinValue;
    private DateTime _directTemperatureRetryAfter = DateTime.MinValue;
    private DateTime _directFrequencyRetryAfter = DateTime.MinValue;
    private DateTime _hardwareRetryAfter = DateTime.MinValue;
    private int _samplingResetRequested = 1;
    private int _hardwareResetRequested;
    private bool _networkBaselineInvalid = true;
    private bool _disposed;

    public MetricsService()
    {
        RefreshNetworkAdapters();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        PrimeCpuUsageSample();
    }

    public Task<MetricsSnapshot> ReadFullAsync(string temperatureFallback, CancellationToken cancellationToken)
        => ReadAsync(includeFullMetrics: true, temperatureFallback, cancellationToken);

    public Task<MetricsSnapshot> ReadCompactAsync(CancellationToken cancellationToken)
        => ReadAsync(includeFullMetrics: false, "--°C", cancellationToken);

    public void ResetSampling()
    {
        Interlocked.Exchange(ref _samplingResetRequested, 1);
        lock (_networkLock)
        {
            _networkBaselineInvalid = true;
        }
    }

    public void PrepareFullMetrics()
    {
        _lastFrequencyRefresh = DateTime.MinValue;
        _lastTemperatureRefresh = DateTime.MinValue;
        _temperatureRetryAfter = DateTime.MinValue;
        _libreTemperatureRetryAfter = DateTime.MinValue;
        _directTemperatureRetryAfter = DateTime.MinValue;
        _directFrequencyRetryAfter = DateTime.MinValue;
        _hardwareRetryAfter = DateTime.MinValue;
        lock (_networkLock)
        {
            _networkBaselineInvalid = true;
        }
    }

    public void ResetHardware()
    {
        Interlocked.Exchange(ref _hardwareResetRequested, 1);
        ResetSampling();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        _readGate.Wait();
        try
        {
            CloseComputer();
            _intelMsrTemperatureReader?.Dispose();
            _intelMsrTemperatureReader = null;
            _pdhCpuUsageReader.Dispose();
        }
        finally
        {
            _readGate.Release();
            _readGate.Dispose();
        }
    }

    private async Task<MetricsSnapshot> ReadAsync(bool includeFullMetrics, string temperatureFallback, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Read(includeFullMetrics, temperatureFallback, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private MetricsSnapshot Read(bool includeFullMetrics, string temperatureFallback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _samplingResetRequested, 0) != 0)
        {
            PrimeCpuUsageSample();
            PrepareFullMetrics();
        }

        if (Interlocked.Exchange(ref _hardwareResetRequested, 0) != 0)
        {
            CloseComputer();
            _intelMsrTemperatureReader?.Dispose();
            _intelMsrTemperatureReader = null;
        }

        var now = DateTime.UtcNow;
        var hardwareUpdated = false;
        if (includeFullMetrics && now - _lastFrequencyRefresh >= FrequencyRefreshInterval)
        {
            _cachedFrequency = ReadCpuFrequency(now, ref hardwareUpdated);
            _lastFrequencyRefresh = now;
        }

        if (includeFullMetrics
            && now - _lastTemperatureRefresh >= TemperatureRefreshInterval
            && now >= _temperatureRetryAfter)
        {
            _cachedTemperature = ReadCpuTemperature(temperatureFallback, now, ref hardwareUpdated);
            _lastTemperatureRefresh = now;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var memory = ReadMemory();
        var cpuUsage = ReadCpuUsage();
        if (!includeFullMetrics)
        {
            return new MetricsSnapshot(cpuUsage, _cachedFrequency, _cachedTemperature, memory.Percent, memory.Free, "--B/s", "--B/s");
        }

        var network = ReadNetwork();
        return new MetricsSnapshot(cpuUsage, _cachedFrequency, _cachedTemperature, memory.Percent, memory.Free, network.Up, network.Down);
    }

    private int ReadCpuUsage()
    {
        var systemTimesUsage = ReadSystemTimesCpuUsage();
        return _pdhCpuUsageReader.Read() ?? systemTimesUsage;
    }

    private int ReadSystemTimesCpuUsage()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return 0;
            }

            var idleTime = ToUInt64(idle);
            var kernelTime = ToUInt64(kernel);
            var userTime = ToUInt64(user);
            if (_lastKernelTime == 0 && _lastUserTime == 0)
            {
                _lastIdleTime = idleTime;
                _lastKernelTime = kernelTime;
                _lastUserTime = userTime;
                return 0;
            }

            var idleDelta = idleTime - _lastIdleTime;
            var kernelDelta = kernelTime - _lastKernelTime;
            var userDelta = userTime - _lastUserTime;
            var totalDelta = kernelDelta + userDelta;

            _lastIdleTime = idleTime;
            _lastKernelTime = kernelTime;
            _lastUserTime = userTime;
            if (totalDelta == 0)
            {
                return 0;
            }

            var usage = 100.0 * (totalDelta - idleDelta) / totalDelta;
            return Math.Clamp((int)Math.Round(usage), 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private void PrimeCpuUsageSample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return;
        }

        _lastIdleTime = ToUInt64(idle);
        _lastKernelTime = ToUInt64(kernel);
        _lastUserTime = ToUInt64(user);
    }

    private string ReadCpuFrequency(DateTime now, ref bool hardwareUpdated)
    {
        string? frequency = null;
        if (now >= _directFrequencyRetryAfter)
        {
            frequency = ReadDirectIntelMsrFrequency();
            _directFrequencyRetryAfter = frequency is null ? now + SensorRetryInterval : DateTime.MinValue;
        }

        var value = frequency ?? ReadLibreHardwareMonitorCpuFrequency(now, ref hardwareUpdated);
        if (value is null) return _cachedFrequency;
        if (double.TryParse(value.Replace("GHz", "", StringComparison.OrdinalIgnoreCase), out var ghz))
        {
            _frequencySamples.Enqueue(ghz);
            while (_frequencySamples.Count > 5) _frequencySamples.Dequeue();
            return $"{_frequencySamples.Average():0.00}GHz";
        }
        return value;
    }

    private string? ReadDirectIntelMsrFrequency()
    {
        try
        {
            _intelMsrTemperatureReader ??= new IntelMsrTemperatureReader();
            var frequency = _intelMsrTemperatureReader.ReadAverageCoreFrequency();
            return frequency is > 100 and < 8000 ? FormatFrequency(frequency.Value) : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadLibreHardwareMonitorCpuFrequency(DateTime now, ref bool hardwareUpdated)
    {
        if (!EnsureCpuHardwareUpdated(now, ref hardwareUpdated))
        {
            return null;
        }

        try
        {
            var selectedSensor = SelectPreferredCpuClockSensor(_computer!.Hardware
                .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
                .SelectMany(EnumerateSensors)
                .Where(sensor => sensor.SensorType == SensorType.Clock));

            var mhz = selectedSensor?.Value;
            return mhz is > 100 and < 8000 ? FormatFrequency(mhz.Value) : null;
        }
        catch
        {
            return null;
        }
    }

    private string ReadCpuTemperature(string fallback, DateTime now, ref bool hardwareUpdated)
    {
        string? temperature = null;
        if (now >= _directTemperatureRetryAfter)
        {
            temperature = ReadDirectIntelMsrTemperature();
            _directTemperatureRetryAfter = temperature is null ? now + SensorRetryInterval : DateTime.MinValue;
        }

        if (temperature is null && now >= _libreTemperatureRetryAfter)
        {
            temperature = ReadLibreHardwareMonitorTemperature(now, ref hardwareUpdated);
            _libreTemperatureRetryAfter = temperature is null ? now + SensorRetryInterval : DateTime.MinValue;
        }

        if (temperature is not null)
        {
            _temperatureRetryAfter = DateTime.MinValue;
            return temperature;
        }

        _temperatureRetryAfter = now + SensorRetryInterval;
        return fallback;
    }

    private string? ReadLibreHardwareMonitorTemperature(DateTime now, ref bool hardwareUpdated)
    {
        if (!EnsureCpuHardwareUpdated(now, ref hardwareUpdated))
        {
            return null;
        }

        try
        {
            var temperature = _computer!.Hardware
                .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
                .SelectMany(EnumerateSensors)
                .Where(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is > 0 and < 120)
                .OrderByDescending(sensor => IsPackageSensor(sensor.Name))
                .ThenByDescending(sensor => sensor.Value)
                .Select(sensor => (double)sensor.Value!.Value)
                .DefaultIfEmpty(double.NaN)
                .First();

            return double.IsNaN(temperature) ? null : $"{temperature:0}°C";
        }
        catch
        {
            return null;
        }
    }

    private string? ReadDirectIntelMsrTemperature()
    {
        try
        {
            _intelMsrTemperatureReader ??= new IntelMsrTemperatureReader();
            var temperature = _intelMsrTemperatureReader.ReadPackageTemperature();
            if (temperature is > 0 and < 120)
            {
                return $"{temperature.Value:0}°C";
            }

            _intelMsrTemperatureReader.Dispose();
            _intelMsrTemperatureReader = null;
            return null;
        }
        catch
        {
            _intelMsrTemperatureReader?.Dispose();
            _intelMsrTemperatureReader = null;
            return null;
        }
    }

    private bool EnsureCpuHardwareUpdated(DateTime now, ref bool hardwareUpdated)
    {
        if (hardwareUpdated)
        {
            return _computer is not null;
        }

        if (now < _hardwareRetryAfter)
        {
            return false;
        }

        try
        {
            if (_computer is null)
            {
                _computer = new Computer { IsCpuEnabled = true };
                _computer.Open();
            }

            _computer.Accept(new UpdateVisitor());
            hardwareUpdated = true;
            return true;
        }
        catch
        {
            CloseComputer();
            _hardwareRetryAfter = now + SensorRetryInterval;
            return false;
        }
    }

    private void CloseComputer()
    {
        try
        {
            _computer?.Close();
        }
        catch
        {
        }

        _computer = null;
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            yield return sensor;
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensors(subHardware))
            {
                yield return sensor;
            }
        }
    }

    private static bool IsPackageSensor(string name)
        => name.Contains("package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("tctl", StringComparison.OrdinalIgnoreCase)
           || name.Contains("tdie", StringComparison.OrdinalIgnoreCase);

    private static bool IsCoreClockSensor(string name)
        => name.Contains("core", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("bus", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("ring", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("uncore", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("memory", StringComparison.OrdinalIgnoreCase);

    private static ISensor? SelectPreferredCpuClockSensor(IEnumerable<ISensor> sensors)
    {
        return sensors
            .Where(sensor => IsCoreClockSensor(sensor.Name))
            .Select(sensor => new { Sensor = sensor, Priority = GetCpuClockSensorPriority(sensor.Name) })
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
    {
        if (mhz <= 0)
        {
            return "--GHz";
        }

        return mhz >= 1000 ? $"{mhz / 1000:0.00}GHz" : $"{mhz:0}MHz";
    }

    private static (int Percent, string Free) ReadMemory()
    {
        var status = new MemoryStatus();
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return (0, "--GB");
        }

        var used = 1.0 - (double)status.ullAvailPhys / status.ullTotalPhys;
        return (Math.Clamp((int)Math.Round(used * 100), 0, 100), $"{status.ullAvailPhys / 1024d / 1024d / 1024d:0.00}GB");
    }

    private (string Up, string Down) ReadNetwork()
    {
        try
        {
            lock (_networkLock)
            {
                var stats = _networkAdapters
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .Select(adapter => adapter.GetIPv4Statistics())
                    .Aggregate((Sent: 0L, Received: 0L), (total, item) => (total.Sent + item.BytesSent, total.Received + item.BytesReceived));

                var now = DateTime.UtcNow;
                if (_networkBaselineInvalid || stats.Sent < _lastSent || stats.Received < _lastReceived)
                {
                    _lastSent = stats.Sent;
                    _lastReceived = stats.Received;
                    _lastNetworkAt = now;
                    _networkBaselineInvalid = false;
                    return ("0B/s", "0B/s");
                }

                var seconds = Math.Max(0.2, (now - _lastNetworkAt).TotalSeconds);
                var up = Math.Max(0, (stats.Sent - _lastSent) / seconds);
                var down = Math.Max(0, (stats.Received - _lastReceived) / seconds);
                var reportedCapacity = _networkAdapters
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .Sum(adapter => adapter.Speed > 0 ? adapter.Speed / 8d : UnknownAdapterCapacityBytesPerSecond);
                var reasonableLimit = Math.Max(reportedCapacity, UnknownAdapterCapacityBytesPerSecond) * 1.5;
                if (up > reasonableLimit || down > reasonableLimit)
                {
                    _lastSent = stats.Sent;
                    _lastReceived = stats.Received;
                    _lastNetworkAt = now;
                    return ("0B/s", "0B/s");
                }

                _lastSent = stats.Sent;
                _lastReceived = stats.Received;
                _lastNetworkAt = now;
                return (FormatSpeed(up), FormatSpeed(down));
            }
        }
        catch
        {
            lock (_networkLock)
            {
                _networkBaselineInvalid = true;
            }

            return ("--B/s", "--B/s");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => RefreshNetworkAdapters();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RefreshNetworkAdapters();

    private void RefreshNetworkAdapters()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToArray();
            lock (_networkLock)
            {
                _networkAdapters = adapters;
                _networkBaselineInvalid = true;
            }
        }
        catch
        {
            lock (_networkLock)
            {
                _networkAdapters = [];
                _networkBaselineInvalid = true;
            }
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024 / 1024:0.0}MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0}KB/s";
        }

        return $"{bytesPerSecond:0}B/s";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);


    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MemoryStatus()
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatus>();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }


    private static ulong ToUInt64(FileTime time)
        => ((ulong)time.HighDateTime << 32) | time.LowDateTime;

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private sealed class PdhCpuUsageReader : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhCstatusValidData = 0x00000000;
        private const uint PdhCstatusNewData = 0x00000001;
        private IntPtr _query;
        private IntPtr _counter;

        public PdhCpuUsageReader()
        {
            if (PdhOpenQuery(null, UIntPtr.Zero, out _query) != 0
                || PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", UIntPtr.Zero, out _counter) != 0)
            {
                Dispose();
                return;
            }

            PdhCollectQueryData(_query);
        }

        public int? Read()
        {
            if (_query == IntPtr.Zero || _counter == IntPtr.Zero)
            {
                return null;
            }

            if (PdhCollectQueryData(_query) != 0
                || PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out var value) != 0
                || value.CStatus is not (PdhCstatusValidData or PdhCstatusNewData)
                || double.IsNaN(value.DoubleValue))
            {
                return null;
            }

            return Math.Clamp((int)Math.Round(value.DoubleValue), 0, 100);
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
            }

            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint CStatus;
            public double DoubleValue;
        }
    }

}

using System.Globalization;
using System.IO;
using PowerPlanMonitor.App.Models;

namespace PowerPlanMonitor.App.Services;

public sealed class ConfigService
{
    private readonly string _path;

    public ConfigService()
    {
#if DEBUG
        var overrideDir = Environment.GetEnvironmentVariable("POWERPLANMONITOR_CONFIG_DIR");
        var configDir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerPlanMonitor")
            : overrideDir;
#else
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerPlanMonitor");
#endif
        Directory.CreateDirectory(configDir);
        _path = Path.Combine(configDir, "setting.ini");
    }

    public string ConfigPath => _path;

    public AppConfig Load()
    {
        var data = ReadIni(_path);
        var config = new AppConfig
        {
            AutoStart = GetBool(data, "App", "AutoStart", true),
            ShowFloating = GetBool(data, "App", "ShowFloating", true),
            AutoSwitchOnPowerChange = GetBool(data, "Power", "AutoSwitchOnPowerChange", true),
            ACMode = Get(data, "Power", "ACMode", "卓越性能"),
            DCMode = Get(data, "Power", "DCMode", "平衡"),
            Mode1 = Get(data, "Power", "Mode1", "节能"),
            Mode2 = Get(data, "Power", "Mode2", "平衡"),
            Mode3 = Get(data, "Power", "Mode3", "卓越性能"),
            LastModeIndex = GetInt(data, "Power", "LastModeIndex", 0),
            HotkeyEnabled = GetBool(data, "Hotkey", "Enabled", true),
            HotkeyCtrl = GetBool(data, "Hotkey", "Ctrl", true),
            HotkeyAlt = GetBool(data, "Hotkey", "Alt", true),
            HotkeyShift = GetBool(data, "Hotkey", "Shift", false),
            HotkeyWin = GetBool(data, "Hotkey", "Win", false),
            HotkeyKey = Get(data, "Hotkey", "Key", "P"),
            TemperatureFallback = Get(data, "Monitor", "TemperatureFallback", "--°C"),
            FloatingX = GetNullableDouble(data, "FloatingWindow", "X"),
            FloatingY = GetNullableDouble(data, "FloatingWindow", "Y"),
            FloatingAutoDock = GetBool(data, "FloatingWindow", "AutoDock", true),
            FloatingTopmost = GetBool(data, "FloatingWindow", "Topmost", true),
            FloatingOpacity = Math.Clamp(GetDouble(data, "FloatingWindow", "Opacity", 0.94), 0.72, 1.0),
            FloatingScale = Math.Clamp(GetDouble(data, "FloatingWindow", "Scale", 1.0), 0.6, 1.4),
            MonitorIntervalSeconds = Math.Clamp(GetDouble(data, "Monitor", "IntervalSeconds", 1.0), 0.5, 5.0),
            ShowTaskbarClock = GetBool(data, "TaskbarClock", "Enabled", true),
            TaskbarClockTimeZoneId = Get(data, "TaskbarClock", "TimeZoneId", "China Standard Time")
        };

        Save(config);
        return config;
    }

    public void Save(AppConfig config, IReadOnlyList<PowerPlan>? plans = null)
    {
        config.FloatingOpacity = Math.Clamp(config.FloatingOpacity, 0.72, 1.0);
        config.FloatingScale = Math.Clamp(config.FloatingScale, 0.6, 1.4);
        config.MonitorIntervalSeconds = Math.Clamp(config.MonitorIntervalSeconds, 0.5, 5.0);
        var lines = new List<string>
        {
            "[App]",
            $"AutoStart={Bool(config.AutoStart)}",
            $"ShowFloating={Bool(config.ShowFloating)}",
            "StartMinimized=1",
            "",
            "[Power]",
            $"AutoSwitchOnPowerChange={Bool(config.AutoSwitchOnPowerChange)}",
            $"ACMode={config.ACMode}",
            $"DCMode={config.DCMode}",
            $"Mode1={config.Mode1}",
            $"Mode2={config.Mode2}",
            $"Mode3={config.Mode3}",
            $"LastModeIndex={config.LastModeIndex}",
            "",
            "[Hotkey]",
            $"Enabled={Bool(config.HotkeyEnabled)}",
            $"Ctrl={Bool(config.HotkeyCtrl)}",
            $"Alt={Bool(config.HotkeyAlt)}",
            $"Shift={Bool(config.HotkeyShift)}",
            $"Win={Bool(config.HotkeyWin)}",
            $"Key={config.HotkeyKey.Trim().ToUpperInvariant()}",
            "",
            "[Monitor]",
            $"TemperatureFallback={config.TemperatureFallback}",
            $"IntervalSeconds={config.MonitorIntervalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}",
            "",
            "[FloatingWindow]",
            $"X={FormatNullable(config.FloatingX)}",
            $"Y={FormatNullable(config.FloatingY)}",
            $"AutoDock={Bool(config.FloatingAutoDock)}",
            $"Topmost={Bool(config.FloatingTopmost)}",
            $"Opacity={config.FloatingOpacity.ToString("0.00", CultureInfo.InvariantCulture)}",
            $"Scale={config.FloatingScale.ToString("0.00", CultureInfo.InvariantCulture)}",
            "",
            "[TaskbarClock]",
            $"Enabled={Bool(config.ShowTaskbarClock)}",
            $"TimeZoneId={config.TaskbarClockTimeZoneId}",
            "",
            "[PowerPlans]"
        };

        if (plans is not null)
        {
            lines.AddRange(plans.Select(plan => $"{plan.Name}={plan.Guid}"));
        }

        File.WriteAllLines(_path, lines);
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                result.TryAdd(section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0 || section.Length == 0)
            {
                continue;
            }

            result[section][line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return result;
    }

    private static string Get(Dictionary<string, Dictionary<string, string>> data, string section, string key, string fallback)
        => data.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    private static bool GetBool(Dictionary<string, Dictionary<string, string>> data, string section, string key, bool fallback)
        => Get(data, section, key, fallback ? "1" : "0") != "0";

    private static int GetInt(Dictionary<string, Dictionary<string, string>> data, string section, string key, int fallback)
        => int.TryParse(Get(data, section, key, ""), out var value) ? value : fallback;

    private static double GetDouble(Dictionary<string, Dictionary<string, string>> data, string section, string key, double fallback)
        => double.TryParse(Get(data, section, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double? GetNullableDouble(Dictionary<string, Dictionary<string, string>> data, string section, string key)
        => double.TryParse(Get(data, section, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Bool(bool value) => value ? "1" : "0";

    private static string FormatNullable(double? value)
        => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) : "";
}

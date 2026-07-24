namespace PowerPlanMonitor.App.Models;

public sealed class AppConfig
{
    public bool AutoStart { get; set; } = true;
    public bool ShowFloating { get; set; } = true;
    public bool AutoSwitchOnPowerChange { get; set; } = true;
    public string ACMode { get; set; } = "卓越性能";
    public string DCMode { get; set; } = "平衡";
    public string Mode1 { get; set; } = "节能";
    public string Mode2 { get; set; } = "平衡";
    public string Mode3 { get; set; } = "卓越性能";
    public int LastModeIndex { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public bool HotkeyCtrl { get; set; } = true;
    public bool HotkeyAlt { get; set; } = true;
    public bool HotkeyShift { get; set; }
    public bool HotkeyWin { get; set; }
    public string HotkeyKey { get; set; } = "P";
    public string TemperatureFallback { get; set; } = "--°C";
    public double? FloatingX { get; set; }
    public double? FloatingY { get; set; }
    public bool FloatingAutoDock { get; set; } = true;
    public bool FloatingTopmost { get; set; } = true;
    public double FloatingOpacity { get; set; } = 0.94;
    public double FloatingScale { get; set; } = 1.0;
    public double MonitorIntervalSeconds { get; set; } = 1.0;
    public bool ShowTaskbarClock { get; set; } = true;
    public string TaskbarClockTimeZoneId { get; set; } = "China Standard Time";
}

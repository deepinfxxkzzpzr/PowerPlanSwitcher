using System.Windows;
using System.Windows.Controls;
using PowerPlanMonitor.App.Models;
using PowerPlanMonitor.App.Services;

namespace PowerPlanMonitor.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly IReadOnlyList<PowerPlan> _plans;
    private readonly PawnIoService _pawnIoService;

    public event EventHandler<AppConfig>? SettingsSaved;
    public event EventHandler? HardwareMonitoringChanged;

    public void SetHotkeyEnabled(bool enabled)
    {
        _config.HotkeyEnabled = enabled;
        HotkeyToggle.IsChecked = enabled;
    }

    public SettingsWindow(AppConfig config, ConfigService configService, IReadOnlyList<PowerPlan> plans, PawnIoService pawnIoService)
    {
        _config = Copy(config);
        _plans = plans;
        _pawnIoService = pawnIoService;
        InitializeComponent();
        LoadValues();
        SaveButton.Click += (_, _) => SaveValues();
        CloseButton.Click += (_, _) => Close();
        InstallPawnIoButton.Click += async (_, _) => await InstallPawnIoAsync();
        UninstallPawnIoButton.Click += async (_, _) => await UninstallPawnIoAsync();
        ScaleSlider.ValueChanged += (_, _) => UpdateScaleText();
        MonitorIntervalSlider.ValueChanged += (_, _) => UpdateMonitorIntervalText();
    }

    private void LoadValues()
    {
        AutoSwitchToggle.IsChecked = _config.AutoSwitchOnPowerChange;
        FloatingToggle.IsChecked = _config.ShowFloating;
        AutoDockToggle.IsChecked = _config.FloatingAutoDock;
        TopLayerRadio.IsChecked = _config.FloatingTopmost;
        BottomLayerRadio.IsChecked = !_config.FloatingTopmost;
        HotkeyToggle.IsChecked = _config.HotkeyEnabled;
        AutoStartToggle.IsChecked = _config.AutoStart;
        CtrlToggle.IsChecked = _config.HotkeyCtrl;
        AltToggle.IsChecked = _config.HotkeyAlt;
        ShiftToggle.IsChecked = _config.HotkeyShift;
        WinToggle.IsChecked = _config.HotkeyWin;
        HotkeyKeyBox.Text = _config.HotkeyKey;
        OpacitySlider.Value = Math.Clamp(_config.FloatingOpacity, 0.72, 1.0);
        ScaleSlider.Value = Math.Clamp(_config.FloatingScale, 0.6, 1.4);
        MonitorIntervalSlider.Value = Math.Clamp(_config.MonitorIntervalSeconds, 0.5, 5.0);
        TaskbarClockToggle.IsChecked = _config.ShowTaskbarClock;
        TimeZoneCombo.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        TimeZoneCombo.SelectedValue = _config.TaskbarClockTimeZoneId;
        if (TimeZoneCombo.SelectedItem is null)
        {
            TimeZoneCombo.SelectedValue = "China Standard Time";
        }
        UpdateScaleText();
        UpdateMonitorIntervalText();

        FillCombo(AcCombo, _config.ACMode);
        FillCombo(DcCombo, _config.DCMode);
        FillCombo(Mode1Combo, _config.Mode1);
        FillCombo(Mode2Combo, _config.Mode2);
        FillCombo(Mode3Combo, _config.Mode3);
        RefreshPawnIoStatus();
    }

    private void RefreshPawnIoStatus()
    {
        var status = _pawnIoService.GetStatus();
        PawnIoStatusText.Text = status.DisplayText;
        InstallPawnIoButton.IsEnabled = status.InstallerAvailable;
        InstallPawnIoButton.Content = status.IsReady ? "重新安装驱动" : "安装/修复驱动";
        UninstallPawnIoButton.IsEnabled = status.IsInstalled;
    }

    private async Task UninstallPawnIoAsync()
    {
        if (System.Windows.MessageBox.Show(this, "确定要卸载 PawnIO 硬件温度驱动吗？", "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        InstallPawnIoButton.IsEnabled = false;
        UninstallPawnIoButton.IsEnabled = false;
        PawnIoStatusText.Text = "正在卸载 PawnIO 驱动...";
        var result = await _pawnIoService.UninstallAsync();
        RefreshPawnIoStatus();
        System.Windows.MessageBox.Show(this, result.Message, result.Success ? "卸载完成" : "卸载失败", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (result.Success) HardwareMonitoringChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task InstallPawnIoAsync()
    {
        InstallPawnIoButton.IsEnabled = false;
        PawnIoStatusText.Text = "正在启动 PawnIO 安装器...";

        var result = await _pawnIoService.InstallOrRepairAsync();
        RefreshPawnIoStatus();
        if (result.Success)
        {
            HardwareMonitoringChanged?.Invoke(this, EventArgs.Empty);
        }

        System.Windows.MessageBox.Show(this, result.Message, result.Success ? "驱动已就绪" : "驱动未就绪", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SaveValues()
    {
        _config.AutoSwitchOnPowerChange = AutoSwitchToggle.IsChecked == true;
        _config.ShowFloating = FloatingToggle.IsChecked == true;
        _config.FloatingAutoDock = AutoDockToggle.IsChecked == true;
        _config.FloatingTopmost = TopLayerRadio.IsChecked == true;
        _config.HotkeyEnabled = HotkeyToggle.IsChecked == true;
        _config.AutoStart = AutoStartToggle.IsChecked == true;
        _config.HotkeyCtrl = CtrlToggle.IsChecked == true;
        _config.HotkeyAlt = AltToggle.IsChecked == true;
        _config.HotkeyShift = ShiftToggle.IsChecked == true;
        _config.HotkeyWin = WinToggle.IsChecked == true;
        _config.HotkeyKey = string.IsNullOrWhiteSpace(HotkeyKeyBox.Text) ? "P" : HotkeyKeyBox.Text.Trim().ToUpperInvariant();
        _config.ACMode = Selected(AcCombo);
        _config.DCMode = Selected(DcCombo);
        _config.Mode1 = Selected(Mode1Combo);
        _config.Mode2 = Selected(Mode2Combo);
        _config.Mode3 = Selected(Mode3Combo);
        _config.FloatingOpacity = Math.Clamp(OpacitySlider.Value, 0.72, 1.0);
        _config.FloatingScale = Math.Clamp(ScaleSlider.Value, 0.6, 1.4);
        _config.MonitorIntervalSeconds = Math.Clamp(MonitorIntervalSlider.Value, 0.5, 5.0);
        _config.ShowTaskbarClock = TaskbarClockToggle.IsChecked == true;
        _config.TaskbarClockTimeZoneId = TimeZoneCombo.SelectedValue?.ToString() ?? "China Standard Time";
        SettingsSaved?.Invoke(this, Copy(_config));
    }

    private void UpdateScaleText()
    {
        ScaleValueText.Text = $"{ScaleSlider.Value * 100:0}%";
    }

    private void UpdateMonitorIntervalText()
    {
        MonitorIntervalValueText.Text = $"{MonitorIntervalSlider.Value:0.0} 秒";
    }

    private void FillCombo(System.Windows.Controls.ComboBox combo, string selected)
    {
        combo.Items.Clear();
        foreach (var plan in _plans)
        {
            combo.Items.Add(plan.Name);
        }
        combo.SelectedItem = selected;
        if (combo.SelectedItem is null && combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static string Selected(System.Windows.Controls.ComboBox combo)
        => combo.SelectedItem?.ToString() ?? "";

    private static AppConfig Copy(AppConfig config)
        => new()
        {
            AutoStart = config.AutoStart,
            ShowFloating = config.ShowFloating,
            AutoSwitchOnPowerChange = config.AutoSwitchOnPowerChange,
            ACMode = config.ACMode,
            DCMode = config.DCMode,
            Mode1 = config.Mode1,
            Mode2 = config.Mode2,
            Mode3 = config.Mode3,
            LastModeIndex = config.LastModeIndex,
            HotkeyEnabled = config.HotkeyEnabled,
            HotkeyCtrl = config.HotkeyCtrl,
            HotkeyAlt = config.HotkeyAlt,
            HotkeyShift = config.HotkeyShift,
            HotkeyWin = config.HotkeyWin,
            HotkeyKey = config.HotkeyKey,
            TemperatureFallback = config.TemperatureFallback,
            FloatingX = config.FloatingX,
            FloatingY = config.FloatingY,
            FloatingAutoDock = config.FloatingAutoDock,
            FloatingTopmost = config.FloatingTopmost,
            FloatingOpacity = config.FloatingOpacity,
            FloatingScale = config.FloatingScale,
            MonitorIntervalSeconds = config.MonitorIntervalSeconds,
            ShowTaskbarClock = config.ShowTaskbarClock,
            TaskbarClockTimeZoneId = config.TaskbarClockTimeZoneId
        };
}

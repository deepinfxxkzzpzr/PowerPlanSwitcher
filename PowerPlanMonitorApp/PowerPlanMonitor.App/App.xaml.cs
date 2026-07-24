using System.IO;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using PowerPlanMonitor.App.Models;
using PowerPlanMonitor.App.Services;

namespace PowerPlanMonitor.App;

public partial class App : System.Windows.Application
{
#if DEBUG
    private const string SingleInstanceMutexName = @"Local\PowerPlanMonitor.SingleInstance.Debug";
#else
    private const string SingleInstanceMutexName = @"Local\PowerPlanMonitor.SingleInstance";
#endif
    private static readonly TimeSpan FloatingReleaseDelay = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _floatingWindowGate = new(1, 1);
    private readonly Dictionary<string, Forms.ToolStripMenuItem> _planMenuItems = new(StringComparer.OrdinalIgnoreCase);

    private Mutex? _singleInstanceMutex;
    private ConfigService _configService = null!;
    private PowerPlanService _powerPlanService = null!;
    private StartupService _startupService = null!;
    private HotkeyService _hotkeyService = null!;
    private PowerStatusService _powerStatusService = null!;
    private PawnIoService _pawnIoService = null!;
    private NativeMessageWindow? _messageWindow;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _floatingMenuItem;
    private MainWindow? _floatingWindow;
    private MetricsService? _metricsService;
    private SettingsWindow? _settingsWindow;
    private TaskbarClockWindow? _taskbarClockWindow;
    private CancellationTokenSource? _floatingReleaseCts;
    private DispatcherTimer? _powerFallbackTimer;
    private AppConfig _config = new();
    private int? _lastAcState;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            _configService = new ConfigService();
            _powerPlanService = new PowerPlanService();
            _startupService = new StartupService();
            _hotkeyService = new HotkeyService();
            _powerStatusService = new PowerStatusService();
            _pawnIoService = new PawnIoService();

            _config = _configService.Load();
            var plans = await _powerPlanService.RefreshAsync();
            NormalizePlanNames(plans);
            _configService.Save(_config, plans);

            _messageWindow = new NativeMessageWindow();
            _hotkeyService.Pressed += OnHotkeyPressed;
            _ = RegisterHotkeyOrDisable(showError: true);

            _powerStatusService.PowerSourceChanged += OnPowerSourceChanged;
            _powerStatusService.SystemResumed += OnSystemResumed;
            if (!_powerStatusService.Initialize(_messageWindow.Handle))
            {
                StartPowerStatusFallback();
            }

            _lastAcState = ReadCurrentAcState();
            CreateTray();
            UpdateTaskbarClock();
            if (_config.ShowFloating)
            {
                await ShowFloatingWindowAsync();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"PowerPlanMonitor 启动失败：{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CancelFloatingRelease();
        _powerFallbackTimer?.Stop();
        _powerFallbackTimer = null;

        if (_hotkeyService is not null)
        {
            _hotkeyService.Pressed -= OnHotkeyPressed;
            _hotkeyService.Unregister();
        }

        if (_powerStatusService is not null)
        {
            _powerStatusService.PowerSourceChanged -= OnPowerSourceChanged;
            _powerStatusService.SystemResumed -= OnSystemResumed;
            _powerStatusService.Dispose();
        }

        _floatingWindow?.StopMonitoring();
        _floatingWindow = null;
        _metricsService?.Dispose();
        _metricsService = null;

        _settingsWindow?.Close();
        _settingsWindow = null;

        _taskbarClockWindow?.Close();
        _taskbarClockWindow = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip = null;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _messageWindow?.Dispose();
        _messageWindow = null;
        _powerPlanService?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
        => await RunSafelyAsync(CyclePowerPlanAsync);

    private async void OnPowerSourceChanged(object? sender, bool pluggedIn)
        => await RunSafelyAsync(() => SwitchForPowerStateAsync(pluggedIn));

    private async void OnSystemResumed(object? sender, EventArgs e)
    {
        _lastAcState = ReadCurrentAcState();
        if (_floatingWindow is not null)
        {
            await RunSafelyAsync(_floatingWindow.ResetAfterResumeAsync);
        }
    }

    private async Task CyclePowerPlanAsync()
    {
        var modes = new[] { _config.Mode1, _config.Mode2, _config.Mode3 };
        var active = await _powerPlanService.GetActiveNameAsync();
        var index = Array.FindIndex(modes, item => string.Equals(item, active, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = _config.LastModeIndex;
        }

        var next = (index + 1) % modes.Length;
        var switched = await _powerPlanService.SetActiveAsync(modes[next]);
        if (switched is null)
        {
            return;
        }

        _config.LastModeIndex = next;
        _configService.Save(_config, _powerPlanService.Plans);
        await CompletePowerPlanSwitchAsync(switched);
    }

    private async Task SwitchForPowerStateAsync(bool pluggedIn)
    {
        var state = pluggedIn ? 1 : 0;
        if (_lastAcState is null)
        {
            _lastAcState = state;
            return;
        }

        if (_lastAcState == state)
        {
            return;
        }

        _lastAcState = state;
        if (!_config.AutoSwitchOnPowerChange)
        {
            return;
        }

        var target = pluggedIn ? _config.ACMode : _config.DCMode;
        var switched = await _powerPlanService.SetActiveAsync(target);
        if (switched is not null)
        {
            await CompletePowerPlanSwitchAsync(switched);
        }
    }

    private async Task CompletePowerPlanSwitchAsync(PowerPlan switched)
    {
        ShowToast(switched.Name);
        UpdateTray();
        if (_floatingWindow is not null)
        {
            await _floatingWindow.RefreshActivePowerPlanAsync();
        }
    }

    private void CreateTray()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        BuildTrayMenu();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Visible = true,
            Text = "PowerPlanMonitor",
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
        UpdateTray();
    }

    private void BuildTrayMenu()
    {
        if (_trayMenu is null)
        {
            return;
        }

        foreach (var item in _trayMenu.Items.Cast<Forms.ToolStripItem>().ToArray())
        {
            item.Dispose();
        }

        _trayMenu.Items.Clear();
        _planMenuItems.Clear();
        _trayMenu.Items.Add("PowerPlanMonitor").Enabled = false;
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        foreach (var plan in _powerPlanService.Plans)
        {
            var item = new Forms.ToolStripMenuItem(plan.Name) { Tag = plan.Guid };
            item.Click += OnTrayPlanClicked;
            _planMenuItems[plan.Guid] = item;
            _trayMenu.Items.Add(item);
        }

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("循环切换三模式", null, async (_, _) => await RunSafelyAsync(CyclePowerPlanAsync));
        _trayMenu.Items.Add("安装/修复温度驱动", null, async (_, _) => await RunSafelyAsync(InstallPawnIoFromTrayAsync));
        _floatingMenuItem = new Forms.ToolStripMenuItem();
        _floatingMenuItem.Click += async (_, _) => await RunSafelyAsync(ToggleFloatingWindowAsync);
        _trayMenu.Items.Add(_floatingMenuItem);
        _trayMenu.Items.Add("设置", null, (_, _) => OpenSettings());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => Shutdown());
    }

    private async void OnTrayPlanClicked(object? sender, EventArgs e)
    {
        if (sender is not Forms.ToolStripMenuItem { Tag: string guid })
        {
            return;
        }

        await RunSafelyAsync(async () =>
        {
            var plan = _powerPlanService.Plans.FirstOrDefault(item => string.Equals(item.Guid, guid, StringComparison.OrdinalIgnoreCase));
            if (plan is null)
            {
                return;
            }

            var switched = await _powerPlanService.SetActiveAsync(plan.Name);
            if (switched is not null)
            {
                await CompletePowerPlanSwitchAsync(switched);
            }
        });
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(processPath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

    private void UpdateTray()
    {
        if (_notifyIcon is null || _trayMenu is null)
        {
            return;
        }

        var currentGuids = _powerPlanService.Plans.Select(plan => plan.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_planMenuItems.Count != currentGuids.Count || _planMenuItems.Keys.Any(guid => !currentGuids.Contains(guid)))
        {
            BuildTrayMenu();
            _notifyIcon.ContextMenuStrip = _trayMenu;
        }

        foreach (var plan in _powerPlanService.Plans)
        {
            if (_planMenuItems.TryGetValue(plan.Guid, out var item))
            {
                item.Text = plan.IsActive ? $"✓ {plan.Name}" : plan.Name;
            }
        }

        if (_floatingMenuItem is not null)
        {
            _floatingMenuItem.Text = _config.ShowFloating ? "隐藏浮窗" : "显示浮窗";
        }
    }

    private async Task ToggleFloatingWindowAsync()
    {
        _config.ShowFloating = !_config.ShowFloating;
        _configService.Save(_config, _powerPlanService.Plans);
        if (_config.ShowFloating)
        {
            await ShowFloatingWindowAsync();
        }
        else
        {
            await HideFloatingWindowAsync();
        }

        UpdateTray();
    }

    private async Task ShowFloatingWindowAsync()
    {
        await _floatingWindowGate.WaitAsync();
        try
        {
            CancelFloatingRelease();
            if (_floatingWindow is null)
            {
                _metricsService = new MetricsService();
                _floatingWindow = new MainWindow(_config, _configService, _metricsService, _powerPlanService, _powerStatusService);
                _floatingWindow.PowerPlanChanged += OnFloatingPowerPlanChanged;
            }

            _floatingWindow.ApplyConfig(_config);
            _floatingWindow.Show();
            _floatingWindow.Activate();
            await _floatingWindow.StartMonitoringAsync();
        }
        finally
        {
            _floatingWindowGate.Release();
        }
    }

    private async Task HideFloatingWindowAsync()
    {
        CancellationTokenSource? releaseCts = null;
        await _floatingWindowGate.WaitAsync();
        try
        {
            CancelFloatingRelease();
            if (_floatingWindow is null)
            {
                return;
            }

            await _floatingWindow.StopMonitoringAsync();
            _floatingWindow.Hide();
            releaseCts = new CancellationTokenSource();
            _floatingReleaseCts = releaseCts;
        }
        finally
        {
            _floatingWindowGate.Release();
        }

        if (releaseCts is not null)
        {
            _ = ReleaseFloatingWindowAfterDelayAsync(releaseCts);
        }
    }

    private async Task ReleaseFloatingWindowAfterDelayAsync(CancellationTokenSource releaseCts)
    {
        try
        {
            await Task.Delay(FloatingReleaseDelay, releaseCts.Token);
            await Dispatcher.InvokeAsync(() => ReleaseFloatingWindowAsync(releaseCts)).Task.Unwrap();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReleaseFloatingWindowAsync(CancellationTokenSource releaseCts)
    {
        await _floatingWindowGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_floatingReleaseCts, releaseCts) || _config.ShowFloating)
            {
                return;
            }

            _floatingReleaseCts = null;
            releaseCts.Dispose();
            if (_floatingWindow is not null)
            {
                _floatingWindow.PowerPlanChanged -= OnFloatingPowerPlanChanged;
                _floatingWindow.Close();
                _floatingWindow = null;
            }

            _metricsService?.Dispose();
            _metricsService = null;
        }
        finally
        {
            _floatingWindowGate.Release();
        }
    }

    private void CancelFloatingRelease()
    {
        var releaseCts = Interlocked.Exchange(ref _floatingReleaseCts, null);
        if (releaseCts is null)
        {
            return;
        }

        releaseCts.Cancel();
        releaseCts.Dispose();
    }

    private void OnFloatingPowerPlanChanged(object? sender, EventArgs e) => UpdateTray();

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _configService, _powerPlanService.Plans, _pawnIoService);
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _settingsWindow.HardwareMonitoringChanged += OnHardwareMonitoringChanged;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private async void OnSettingsSaved(object? sender, AppConfig config)
    {
        await RunSafelyAsync(async () =>
        {
            var wasAutoSwitchEnabled = _config.AutoSwitchOnPowerChange;
            _config = config;
            _configService.Save(_config, _powerPlanService.Plans);
            _startupService.SetEnabled(_config.AutoStart);
            if (!wasAutoSwitchEnabled && _config.AutoSwitchOnPowerChange)
            {
                _lastAcState = ReadCurrentAcState();
            }

            if (_messageWindow is not null)
            {
                var hotkeyRegistered = RegisterHotkeyOrDisable(showError: true);
                if (!hotkeyRegistered && sender is SettingsWindow settingsWindow)
                {
                    settingsWindow.SetHotkeyEnabled(false);
                }
            }

            if (_config.ShowFloating)
            {
                await ShowFloatingWindowAsync();
            }
            else
            {
                await HideFloatingWindowAsync();
            }

            UpdateTaskbarClock();

            UpdateTray();
        });
    }

    private void UpdateTaskbarClock()
    {
        if (!_config.ShowTaskbarClock)
        {
            _taskbarClockWindow?.Close();
            _taskbarClockWindow = null;
            return;
        }

        if (_taskbarClockWindow is null)
        {
            _taskbarClockWindow = new TaskbarClockWindow();
        }

        _taskbarClockWindow.ApplyTimeZone(_config.TaskbarClockTimeZoneId);
        _taskbarClockWindow.Show();
    }

    private async void OnHardwareMonitoringChanged(object? sender, EventArgs e)
    {
        if (_floatingWindow is not null)
        {
            await RunSafelyAsync(_floatingWindow.ResetAfterResumeAsync);
        }
    }

    private async Task InstallPawnIoFromTrayAsync()
    {
        var result = await _pawnIoService.InstallOrRepairAsync();
        System.Windows.MessageBox.Show(result.Message, result.Success ? "驱动已就绪" : "驱动未就绪", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (result.Success && _floatingWindow is not null)
        {
            await _floatingWindow.ResetAfterResumeAsync();
        }
    }

    private void ShowToast(string message) => new ToastWindow(message).Show();

    private bool RegisterHotkeyOrDisable(bool showError)
    {
        if (_messageWindow is null || _hotkeyService.Register(_messageWindow.Handle, _config))
        {
            return true;
        }

        _config.HotkeyEnabled = false;
        _configService.Save(_config, _powerPlanService.Plans);
        if (showError)
        {
            System.Windows.MessageBox.Show(
                "全局快捷键已被其他程序占用，本次注册失败，快捷键功能已自动关闭。请在设置中选择其他组合后重新启用。",
                "快捷键不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private int? ReadCurrentAcState()
    {
        var state = _powerStatusService.GetAcLineStatus();
        return state is 0 or 1 ? state : null;
    }

    private void StartPowerStatusFallback()
    {
        _powerFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _powerFallbackTimer.Tick += (_, _) =>
        {
            var current = ReadCurrentAcState();
            if (current is not null)
            {
                OnPowerSourceChanged(this, current == 1);
            }
        };
        _powerFallbackTimer.Start();
    }

    private static async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // UI and system event handlers must not terminate the tray process.
        }
    }

    private void NormalizePlanNames(IReadOnlyList<PowerPlan> plans)
    {
        _config.ACMode = _powerPlanService.Find(_config.ACMode)?.Name ?? plans.LastOrDefault()?.Name ?? _config.ACMode;
        _config.DCMode = _powerPlanService.Find(_config.DCMode)?.Name ?? plans.FirstOrDefault()?.Name ?? _config.DCMode;
        _config.Mode1 = _powerPlanService.Find(_config.Mode1)?.Name ?? plans.ElementAtOrDefault(0)?.Name ?? _config.Mode1;
        _config.Mode2 = _powerPlanService.Find(_config.Mode2)?.Name ?? plans.ElementAtOrDefault(1)?.Name ?? _config.Mode2;
        _config.Mode3 = _powerPlanService.Find(_config.Mode3)?.Name ?? plans.ElementAtOrDefault(2)?.Name ?? _config.Mode3;
    }
}

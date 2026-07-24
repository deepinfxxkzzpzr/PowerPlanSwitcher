using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PowerPlanMonitor.App.Models;
using PowerPlanMonitor.App.Services;
using MediaColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WindowsPoint = System.Windows.Point;

namespace PowerPlanMonitor.App;

public partial class MainWindow : Window
{
    private const double BaseWidth = 572;
    private const double BaseHeight = 260;
    private const double DockedWidth = 24;
    private const double DockedHeight = 86;
    private const double DockSnapDistance = 26;
    private const double DockDragDistance = 10;
    private const double DockStripFillHeight = 78;
    private const double GaugeCenterX = 105;
    private const double GaugeCenterY = 106;
    private const double GaugeRadius = 82;
    private const double GaugeStartAngle = 180;
    private const double GaugeSweepAngle = 180;
    private const double GaugeMarkerInnerRadius = 76;
    private const double GaugeMarkerOuterRadius = 88;
    private static readonly Geometry[] GaugeGeometryCache = Enumerable.Range(0, 101).Select(CreateArcGeometry).ToArray();
    private static readonly Geometry[] GaugeMarkerGeometryCache = Enumerable.Range(0, 101).Select(CreateArcEndMarkerGeometry).ToArray();

    private readonly ConfigService _configService;
    private readonly MetricsService _metricsService;
    private readonly PowerPlanService _powerPlanService;
    private readonly PowerStatusService _powerStatusService;
    private readonly DispatcherTimer _timer = new();
    private readonly SolidColorBrush _cpuArcBrush = new();
    private readonly SolidColorBrush _memoryArcBrush = new();
    private readonly SolidColorBrush _temperatureBrush = new();
    private readonly SolidColorBrush _networkBrush = new();
    private readonly SolidColorBrush _powerStateBrush = new();
    private AppConfig _config;
    private CancellationTokenSource _refreshCts = new();
    private Task _activeRefresh = Task.CompletedTask;
    private bool _isMonitoring;
    private bool _isClosed;
    private bool _busy;
    private bool _switchingPlan;
    private bool _updatingPlanPicker;
    private bool _isDocked;
    private bool _isChangingWindowMode;
    private bool _dockPointerDown;
    private bool _dockDragStarted;
    private double _normalLeft;
    private double _normalTop;
    private DockEdge _dockEdge = DockEdge.Right;
    private WindowsPoint _dockPointerStart;
    private int _lastCpuUsage = -1;
    private int _lastMemoryPercent = -1;
    private string _lastTemperature = "";

    public event EventHandler? PowerPlanChanged;

    public MainWindow(AppConfig config, ConfigService configService, MetricsService metricsService, PowerPlanService powerPlanService, PowerStatusService powerStatusService)
    {
        _config = config;
        _configService = configService;
        _metricsService = metricsService;
        _powerPlanService = powerPlanService;
        _powerStatusService = powerStatusService;
        InitializeComponent();
        CpuTrack.Data = GaugeGeometryCache[100];
        MemoryTrack.Data = GaugeGeometryCache[100];
        CpuArcMarker.Data = Geometry.Empty;
        MemoryArcMarker.Data = Geometry.Empty;
        CpuArc.Stroke = _cpuArcBrush;
        MemoryArc.Stroke = _memoryArcBrush;
        TemperatureText.Foreground = _temperatureBrush;
        NetworkText.Foreground = _networkBrush;
        PowerStateText.Foreground = _powerStateBrush;
        ApplyConfig(config);
        Loaded += async (_, _) => await RunUiActionSafelyAsync(() => PopulatePowerPlansAsync(refreshPlans: false));
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        PlanButton.Click += (_, _) => PlanPopup.IsOpen = !PlanPopup.IsOpen;
        PlanList.SelectionChanged += async (_, _) => await RunUiActionSafelyAsync(SwitchPlanFromPopupAsync);
        _timer.Tick += OnRefreshTick;
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(config.MonitorIntervalSeconds, 0.5, 5.0));
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Opacity = Math.Clamp(config.FloatingOpacity, 0.72, 1.0);
        Topmost = config.FloatingTopmost;

        if (_isDocked && !config.FloatingAutoDock)
        {
            RestoreFromDock(keepNearEdge: false);
        }

        var scale = Math.Clamp(config.FloatingScale, 0.6, 1.4);
        if (_isDocked)
        {
            ApplyDockedBounds();
        }
        else
        {
            ApplyNormalBounds(scale);
        }

        if (!_isDocked)
        {
            var defaultArea = SystemParameters.WorkArea;
            var desiredLeft = config.FloatingX ?? defaultArea.Right - Width - 24;
            var desiredTop = config.FloatingY ?? defaultArea.Top + 24;
            Left = ClampToVirtualDesktop(desiredLeft, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, Width);
            Top = ClampToVirtualDesktop(desiredTop, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, Height);
            _normalLeft = Left;
            _normalTop = Top;
        }

        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(config.MonitorIntervalSeconds, 0.5, 5.0));
    }

    private static double ClampToVirtualDesktop(double position, double virtualStart, double virtualLength, double windowLength)
    {
        var maximum = Math.Max(virtualStart, virtualStart + virtualLength - windowLength);
        return Math.Clamp(position, virtualStart, maximum);
    }

    public void ShowToast(string message)
    {
        new ToastWindow(message).Show();
    }

    public async Task StartMonitoringAsync()
    {
        if (_isClosed)
        {
            return;
        }

        if (!_isMonitoring)
        {
            _refreshCts.Dispose();
            _refreshCts = new CancellationTokenSource();
            _isMonitoring = true;
            _metricsService.ResetSampling();
            _timer.Start();
        }

        await RefreshSafelyAsync();
    }

    public async Task StopMonitoringAsync()
    {
        StopMonitoring();
        try
        {
            await _activeRefresh;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A failed final sample must not block hiding and resource release.
        }

        SavePosition();
    }

    public void StopMonitoring()
    {
        if (_isClosed)
        {
            return;
        }

        _isMonitoring = false;
        _timer.Stop();
        _refreshCts.Cancel();
    }

    public async Task ResetAfterResumeAsync()
    {
        _metricsService.ResetHardware();
        if (_isMonitoring)
        {
            await RefreshSafelyAsync();
        }
    }

    public Task RefreshActivePowerPlanAsync()
        => RunUiActionSafelyAsync(() => SelectActivePowerPlanAsync(allowRefresh: false));

    protected override void OnClosed(EventArgs e)
    {
        StopMonitoring();
        _isClosed = true;
        _timer.Tick -= OnRefreshTick;
        _refreshCts.Dispose();
        base.OnClosed(e);
    }

    private async void OnRefreshTick(object? sender, EventArgs e)
        => await RefreshSafelyAsync();

    private async Task RefreshSafelyAsync()
    {
        if (!_isMonitoring || _busy)
        {
            return;
        }

        _busy = true;
        var refreshTask = RefreshAsync(_refreshCts.Token);
        _activeRefresh = refreshTask;
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep the last valid snapshot and let the next timer tick retry.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_isDocked)
        {
            var compactMetrics = await _metricsService.ReadCompactAsync(cancellationToken);
            UpdateDockedStrips(compactMetrics);
            return;
        }

        var metrics = await _metricsService.ReadFullAsync(_config.TemperatureFallback, cancellationToken);
        SetText(CpuPercentText, $"{metrics.CpuUsage}%");
        SetText(CpuFrequencyText, metrics.CpuFrequency);
        SetText(MemoryPercentText, $"{metrics.MemoryPercent}%");
        SetText(MemoryFreeText, metrics.FreeMemory);
        SetText(TemperatureText, metrics.CpuTemperature);
        SetText(NetworkText, $"↑ {metrics.UploadSpeed}   ↓ {metrics.DownloadSpeed}");

        if (_lastCpuUsage != metrics.CpuUsage)
        {
            UpdateGauge(CpuArc, CpuArcMarker, metrics.CpuUsage);
        }

        if (_lastMemoryPercent != metrics.MemoryPercent)
        {
            UpdateGauge(MemoryArc, MemoryArcMarker, metrics.MemoryPercent);
        }

        if (_lastCpuUsage != metrics.CpuUsage
            || _lastMemoryPercent != metrics.MemoryPercent
            || !string.Equals(_lastTemperature, metrics.CpuTemperature, StringComparison.Ordinal))
        {
            ApplyPressureColors(metrics);
        }

        _lastCpuUsage = metrics.CpuUsage;
        _lastMemoryPercent = metrics.MemoryPercent;
        _lastTemperature = metrics.CpuTemperature;

        var acState = _powerStatusService.GetAcLineStatus();
        SetText(PowerStateText, acState == 1 ? "POWER IN" : acState == 0 ? "ON BATTERY" : "POWER UNKNOWN");
    }

    private static void SetText(TextBlock textBlock, string value)
    {
        if (!string.Equals(textBlock.Text, value, StringComparison.Ordinal))
        {
            textBlock.Text = value;
        }
    }

    private static async Task RunUiActionSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // WPF async event handlers must not terminate the tray process.
        }
    }

    private async Task PopulatePowerPlansAsync(bool refreshPlans)
    {
        if (refreshPlans || _powerPlanService.Plans.Count == 0)
        {
            await _powerPlanService.RefreshAsync();
        }

        _updatingPlanPicker = true;
        try
        {
            PlanList.Items.Clear();
            foreach (var plan in _powerPlanService.Plans)
            {
                PlanList.Items.Add(plan.Name);
            }
        }
        finally
        {
            _updatingPlanPicker = false;
        }

        await SelectActivePowerPlanAsync(allowRefresh: false);
    }

    private async Task SelectActivePowerPlanAsync(bool allowRefresh = true)
    {
        var activeName = await _powerPlanService.GetActiveNameAsync();
        if (string.IsNullOrWhiteSpace(activeName))
        {
            return;
        }

        _updatingPlanPicker = true;
        try
        {
            if (!PlanList.Items.Contains(activeName))
            {
                if (allowRefresh)
                {
                    await PopulatePowerPlansAsync(refreshPlans: true);
                }

                return;
            }

            PlanNameText.Text = activeName;
            PlanList.SelectedItem = activeName;
        }
        finally
        {
            _updatingPlanPicker = false;
        }
    }

    private async Task SwitchPlanFromPopupAsync()
    {
        if (_updatingPlanPicker || _switchingPlan || PlanList.SelectedItem is not string planName || string.IsNullOrWhiteSpace(planName))
        {
            return;
        }

        _switchingPlan = true;
        try
        {
            var plan = await _powerPlanService.SetActiveAsync(planName);
            if (plan is not null)
            {
                PlanPopup.IsOpen = false;
                PlanNameText.Text = plan.Name;
                ShowToast($"已切换到 {plan.Name}");
                await PopulatePowerPlansAsync(refreshPlans: false);
                PowerPlanChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _switchingPlan = false;
        }
    }

    private static void UpdateGauge(System.Windows.Shapes.Path arc, System.Windows.Shapes.Path marker, int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        arc.Data = GaugeGeometryCache[clamped];
        marker.Data = GaugeMarkerGeometryCache[clamped];
    }

    private static Geometry CreateArcGeometry(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped == 0)
        {
            return Geometry.Empty;
        }

        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var steps = Math.Max(2, (int)Math.Ceiling(clamped * 0.9));
        context.BeginFigure(PointOnGauge(GaugeStartAngle), false, false);
        for (var index = 1; index <= steps; index++)
        {
            var progress = (double)index / steps;
            var angle = GaugeStartAngle + (GaugeSweepAngle * clamped / 100.0 * progress);
            context.LineTo(PointOnGauge(angle), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateArcEndMarkerGeometry(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped == 0)
        {
            return Geometry.Empty;
        }

        var angle = PercentToGaugeAngle(clamped);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointOnCircle(GaugeMarkerInnerRadius, angle), false, false);
            context.LineTo(PointOnCircle(GaugeMarkerOuterRadius, angle), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static double PercentToGaugeAngle(int percent)
        => GaugeStartAngle + (GaugeSweepAngle * Math.Clamp(percent, 0, 100) / 100.0);

    private static WindowsPoint PointOnGauge(double degrees)
        => PointOnCircle(GaugeRadius, degrees);

    private static WindowsPoint PointOnCircle(double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new WindowsPoint(
            GaugeCenterX + (radius * Math.Cos(radians)),
            GaugeCenterY + (radius * Math.Sin(radians)));
    }

    private void ApplyPressureColors(MetricsSnapshot metrics)
    {
        var tempPressure = TemperaturePressure(metrics.CpuTemperature);
        var usagePressure = Normalize(Math.Max(metrics.CpuUsage, metrics.MemoryPercent), 35, 95);
        var pressure = Math.Max(tempPressure, usagePressure);
        var accent = PressureColor(pressure);
        var cpuAccent = PressureColor(Normalize(metrics.CpuUsage, 35, 95));
        var memoryAccent = PressureColor(Normalize(metrics.MemoryPercent, 35, 95));

        BackgroundStopA.Color = WithAlpha(LerpColor(MediaColor.FromRgb(8, 20, 30), accent, pressure * 0.12), 248);
        BackgroundStopB.Color = WithAlpha(LerpColor(MediaColor.FromRgb(16, 22, 31), accent, pressure * 0.08), 249);
        BackgroundStopC.Color = WithAlpha(LerpColor(MediaColor.FromRgb(7, 11, 17), accent, pressure * 0.04), 250);
        ShellGlow.Color = accent;
        ShellGlow.Opacity = 0.16 + (pressure * 0.20);

        _cpuArcBrush.Color = cpuAccent;
        _memoryArcBrush.Color = memoryAccent;
        CpuArcGlow.Color = cpuAccent;
        MemoryArcGlow.Color = memoryAccent;
        _temperatureBrush.Color = accent;
        _networkBrush.Color = LerpColor(MediaColor.FromRgb(167, 243, 208), accent, pressure * 0.45);
        _powerStateBrush.Color = LerpColor(MediaColor.FromRgb(167, 243, 208), accent, pressure * 0.35);
    }

    private static double TemperaturePressure(string temperature)
    {
        var match = Regex.Match(temperature, @"-?\d+(\.\d+)?");
        if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return Normalize(value, 55, 95);
    }

    private static double Normalize(double value, double min, double max)
        => Math.Clamp((value - min) / (max - min), 0, 1);

    private static MediaColor PressureColor(double pressure)
    {
        var teal = MediaColor.FromRgb(56, 189, 248);
        var amber = MediaColor.FromRgb(251, 191, 36);
        var red = MediaColor.FromRgb(248, 82, 82);
        return pressure < 0.5
            ? LerpColor(teal, amber, pressure / 0.5)
            : LerpColor(amber, red, (pressure - 0.5) / 0.5);
    }

    private static MediaColor LerpColor(MediaColor from, MediaColor to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return MediaColor.FromRgb(
            (byte)(from.R + ((to.R - from.R) * t)),
            (byte)(from.G + ((to.G - from.G) * t)),
            (byte)(from.B + ((to.B - from.B) * t)));
    }

    private static MediaColor Darken(MediaColor color, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return MediaColor.FromRgb((byte)(color.R * (1 - t)), (byte)(color.G * (1 - t)), (byte)(color.B * (1 - t)));
    }

    private static MediaColor WithAlpha(MediaColor color, byte alpha)
        => MediaColor.FromArgb(alpha, color.R, color.G, color.B);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<WpfButton>(e.OriginalSource as DependencyObject) is not null
            || FindParent<WpfListBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        DragMove();
        TryDockToEdge();
        if (!_isDocked)
        {
            SavePosition();
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed)
            {
                return typed;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void SavePosition()
    {
        if (!IsLoaded || _isDocked || _isChangingWindowMode)
        {
            return;
        }

        _config.FloatingX = Left;
        _config.FloatingY = Top;
        _normalLeft = Left;
        _normalTop = Top;
        _configService.Save(_config, _powerPlanService.Plans);
    }

    private void ApplyNormalBounds(double scale)
    {
        _isChangingWindowMode = true;
        try
        {
            GaugeScale.ScaleX = scale;
            GaugeScale.ScaleY = scale;
            GaugeRoot.Width = BaseWidth;
            GaugeRoot.Height = BaseHeight;
            ShellBorder.Visibility = Visibility.Visible;
            DockedRoot.Visibility = Visibility.Collapsed;
            Width = BaseWidth * scale;
            Height = BaseHeight * scale;
        }
        finally
        {
            _isChangingWindowMode = false;
        }
    }

    private void ApplyDockedBounds()
    {
        _isChangingWindowMode = true;
        try
        {
            GaugeScale.ScaleX = 1;
            GaugeScale.ScaleY = 1;
            GaugeRoot.Width = DockedWidth;
            GaugeRoot.Height = DockedHeight;
            Width = DockedWidth;
            Height = DockedHeight;
            ShellBorder.Visibility = Visibility.Collapsed;
            DockedRoot.Visibility = Visibility.Visible;
        }
        finally
        {
            _isChangingWindowMode = false;
        }
    }

    private void TryDockToEdge()
    {
        if (_isDocked || !_config.FloatingAutoDock)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var nearLeft = Left - area.Left <= DockSnapDistance;
        var nearRight = area.Right - (Left + Width) <= DockSnapDistance;
        if (!nearLeft && !nearRight)
        {
            return;
        }

        DockToEdge(nearLeft ? DockEdge.Left : DockEdge.Right);
    }

    private void DockToEdge(DockEdge edge)
    {
        var area = SystemParameters.WorkArea;
        _dockEdge = edge;
        _normalLeft = Math.Clamp(Left, area.Left, area.Right - Width);
        _normalTop = Math.Clamp(Top, area.Top, area.Bottom - Height);
        SaveNormalPosition();
        _isDocked = true;

        ApplyDockedBounds();
        Left = edge == DockEdge.Left ? area.Left : area.Right - Width;
        Top = Math.Clamp(_normalTop + ((BaseHeight * Math.Clamp(_config.FloatingScale, 0.6, 1.4)) - Height) / 2, area.Top + 8, area.Bottom - Height - 8);
        RequestRefresh();
    }

    private void RestoreFromDock(bool keepNearEdge)
    {
        if (!_isDocked)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        _isDocked = false;
        ApplyNormalBounds(Math.Clamp(_config.FloatingScale, 0.6, 1.4));

        if (keepNearEdge)
        {
            Left = _dockEdge == DockEdge.Left ? area.Left + 14 : area.Right - Width - 14;
            Top = Math.Clamp(Top + ((DockedHeight - Height) / 2), area.Top, area.Bottom - Height);
        }
        else
        {
            Left = Math.Clamp(_normalLeft, area.Left, area.Right - Width);
            Top = Math.Clamp(_normalTop, area.Top, area.Bottom - Height);
        }

        SavePosition();
        _metricsService.PrepareFullMetrics();
        RequestRefresh();
    }

    private void OnDockedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dockPointerDown = true;
        _dockDragStarted = false;
        _dockPointerStart = e.GetPosition(this);
        DockedRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnDockedMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dockPointerDown || e.LeftButton != MouseButtonState.Pressed || _dockDragStarted)
        {
            return;
        }

        var current = e.GetPosition(this);
        var distance = Math.Sqrt(Math.Pow(current.X - _dockPointerStart.X, 2) + Math.Pow(current.Y - _dockPointerStart.Y, 2));
        if (distance < DockDragDistance)
        {
            return;
        }

        _dockDragStarted = true;
        _dockPointerDown = false;
        DockedRoot.ReleaseMouseCapture();
        RestoreFromDock(keepNearEdge: true);

        try
        {
            DragMove();
            TryDockToEdge();
            if (!_isDocked)
            {
                SavePosition();
            }
        }
        catch (InvalidOperationException)
        {
        }

        e.Handled = true;
    }

    private void OnDockedMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dockPointerDown)
        {
            return;
        }

        _dockPointerDown = false;
        DockedRoot.ReleaseMouseCapture();
        if (!_dockDragStarted)
        {
            RestoreFromDock(keepNearEdge: true);
        }

        e.Handled = true;
    }

    private void UpdateDockedStrips(MetricsSnapshot metrics)
    {
        var cpuHeight = DockStripFillHeight * Math.Clamp(metrics.CpuUsage, 0, 100) / 100.0;
        var memoryHeight = DockStripFillHeight * Math.Clamp(metrics.MemoryPercent, 0, 100) / 100.0;
        if (Math.Abs(CpuStripFill.Height - cpuHeight) > 0.01)
        {
            CpuStripFill.Height = cpuHeight;
        }

        if (Math.Abs(MemoryStripFill.Height - memoryHeight) > 0.01)
        {
            MemoryStripFill.Height = memoryHeight;
        }
    }

    private void SaveNormalPosition()
    {
        _config.FloatingX = _normalLeft;
        _config.FloatingY = _normalTop;
        _configService.Save(_config, _powerPlanService.Plans);
    }

    private void RequestRefresh()
    {
        if (_isMonitoring)
        {
            _ = RefreshSafelyAsync();
        }
    }

    private enum DockEdge
    {
        Left,
        Right
    }
}

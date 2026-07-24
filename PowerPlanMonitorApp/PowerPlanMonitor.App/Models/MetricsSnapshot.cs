namespace PowerPlanMonitor.App.Models;

public sealed record MetricsSnapshot(
    int CpuUsage,
    string CpuFrequency,
    string CpuTemperature,
    int MemoryPercent,
    string FreeMemory,
    string UploadSpeed,
    string DownloadSpeed);

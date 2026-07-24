using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PowerPlanMonitor.App.Services;

public sealed class PawnIoService
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const string InstallerFileName = "PawnIO_Setup.exe";

    public PawnIoStatus GetStatus()
    {
        var version = ReadInstalledVersion();
        var deviceAvailable = CanOpenDevice();
        var installerPath = GetInstallerPath();

        return new PawnIoStatus(
            version is not null,
            version,
            deviceAvailable,
            installerPath,
            File.Exists(installerPath));
    }

    public async Task<PawnIoInstallResult> InstallOrRepairAsync()
    {
        var status = GetStatus();
        if (!status.InstallerAvailable)
        {
            return new PawnIoInstallResult(false, "缺少 PawnIO_Setup.exe，无法安装硬件温度驱动。");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = status.InstallerPath,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (process is null)
            {
                return new PawnIoInstallResult(false, "驱动安装器没有启动。");
            }

            await process.WaitForExitAsync();
            var after = GetStatus();
            return after.IsReady
                ? new PawnIoInstallResult(true, "PawnIO 驱动已安装，CPU 温度读取已启用。")
                : new PawnIoInstallResult(false, "安装器已退出，但 PawnIO 设备仍不可用。请重启电脑后再试。");
        }
        catch (Exception ex)
        {
            return new PawnIoInstallResult(false, $"驱动安装失败：{ex.Message}");
        }
    }

    public async Task<PawnIoInstallResult> UninstallAsync()
    {
        var uninstall = ReadUninstallString();
        if (string.IsNullOrWhiteSpace(uninstall))
            return new PawnIoInstallResult(false, "未找到 PawnIO 的卸载信息，驱动可能已经卸载。"
            );
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c {uninstall}", UseShellExecute = true, Verb = "runas"
            });
            if (process is null) return new PawnIoInstallResult(false, "驱动卸载程序没有启动。");
            await process.WaitForExitAsync();
            return GetStatus().IsInstalled
                ? new PawnIoInstallResult(false, "卸载程序已退出，但 PawnIO 仍显示已安装。")
                : new PawnIoInstallResult(true, "PawnIO 驱动已卸载。重新安装前建议重启电脑。" );
        }
        catch (Exception ex) { return new PawnIoInstallResult(false, $"驱动卸载失败：{ex.Message}"); }
    }

    private static string GetInstallerPath()
        => Path.Combine(AppContext.BaseDirectory, InstallerFileName);

    private static Version? ReadInstalledVersion()
    {
        return ReadRegistryVersion(RegistryView.Registry64) ?? ReadRegistryVersion(RegistryView.Registry32);
    }

    private static Version? ReadRegistryVersion(RegistryView view)
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

    private static string? ReadUninstallString()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                var value = key?.GetValue("UninstallString") as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }
        return null;
    }

    private static bool CanOpenDevice()
    {
        var handle = CreateFile(
            DevicePath,
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
}

public sealed record PawnIoStatus(
    bool IsInstalled,
    Version? Version,
    bool DeviceAvailable,
    string InstallerPath,
    bool InstallerAvailable)
{
    public bool IsReady => IsInstalled && DeviceAvailable;

    public string DisplayText
    {
        get
        {
            if (IsReady)
            {
                return $"已启用 PawnIO {Version}";
            }

            if (IsInstalled)
            {
                return $"已安装 PawnIO {Version}，但设备未就绪";
            }

            return "未安装 PawnIO，CPU 温度不可用";
        }
    }
}

public sealed record PawnIoInstallResult(bool Success, string Message);

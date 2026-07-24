using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace PowerPlanMonitor.App.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PowerPlanMonitor";

    public void SetEnabled(bool enabled)
    {
        RemoveLegacyRunEntry();

        if (enabled)
        {
            CreateScheduledTask();
            return;
        }

        DeleteScheduledTask();
    }

    private static void CreateScheduledTask()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        var userName = WindowsIdentity.GetCurrent().Name;
        RunSchtasks(
            "/Create",
            "/TN",
            AppName,
            "/TR",
            $"\"{processPath}\"",
            "/SC",
            "ONLOGON",
            "/RL",
            "HIGHEST",
            "/RU",
            userName,
            "/IT",
            "/F");
    }

    private static void DeleteScheduledTask()
    {
        RunSchtasks("/Delete", "/TN", AppName, "/F");
    }

    private static void RemoveLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static void RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            process?.WaitForExit(5000);
        }
        catch
        {
            // Startup registration should never prevent the main app from opening.
        }
    }
}

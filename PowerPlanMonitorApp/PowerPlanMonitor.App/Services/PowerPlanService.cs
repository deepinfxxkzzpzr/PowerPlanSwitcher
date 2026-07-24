using System.Diagnostics;
using System.Text.RegularExpressions;
using PowerPlanMonitor.App.Models;

namespace PowerPlanMonitor.App.Services;

public sealed class PowerPlanService : IDisposable
{
    private static readonly Regex PlanRegex = new(
        @"([a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}).*\((.+?)\)(.*)$",
        RegexOptions.Compiled);

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private List<PowerPlan> _plans = [];

    public IReadOnlyList<PowerPlan> Plans => _plans;

    public async Task<IReadOnlyList<PowerPlan>> RefreshAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            return await RefreshCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<string> GetActiveNameAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            var active = await GetActivePlanCoreAsync();
            return active?.Name ?? _plans.FirstOrDefault(plan => plan.IsActive)?.Name ?? "";
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public PowerPlan? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var direct = _plans.FirstOrDefault(plan => string.Equals(plan.Name, name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Power saver"] = ["节能", "省电"],
            ["Balanced"] = ["平衡"],
            ["High performance"] = ["高性能"],
            ["Ultimate Performance"] = ["卓越性能"]
        };

        foreach (var (english, localized) in aliases)
        {
            if (!string.Equals(name, english, StringComparison.OrdinalIgnoreCase) && !localized.Contains(name))
            {
                continue;
            }

            return _plans.FirstOrDefault(plan =>
                string.Equals(plan.Name, english, StringComparison.OrdinalIgnoreCase) || localized.Contains(plan.Name));
        }

        return null;
    }

    public async Task<PowerPlan?> SetActiveAsync(string name)
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_plans.Count == 0)
            {
                await RefreshCoreAsync();
            }

            var plan = Find(name);
            if (plan is null)
            {
                return null;
            }

            var active = await GetActivePlanCoreAsync();
            if (active is not null && string.Equals(active.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase))
            {
                return active;
            }

            var result = await RunPowerCfgAsync($"/setactive {plan.Guid}");
            if (!result.Success)
            {
                return null;
            }

            await RefreshCoreAsync();
            return _plans.FirstOrDefault(item => string.Equals(item.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase)) ?? plan;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose() => _operationGate.Dispose();

    private async Task<IReadOnlyList<PowerPlan>> RefreshCoreAsync()
    {
        var result = await RunPowerCfgAsync("/l");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return _plans;
        }

        var refreshed = result.Output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PlanRegex.Match(line))
            .Where(match => match.Success)
            .Select(match => new PowerPlan(match.Groups[2].Value.Trim(), match.Groups[1].Value, match.Groups[3].Value.Contains('*')))
            .ToList();

        if (refreshed.Count > 0)
        {
            _plans = refreshed;
        }

        return _plans;
    }

    private async Task<PowerPlan?> GetActivePlanCoreAsync()
    {
        var result = await RunPowerCfgAsync("/getactivescheme");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        var guidMatch = Regex.Match(result.Output, @"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}");
        if (!guidMatch.Success)
        {
            return null;
        }

        var activeGuid = guidMatch.Value;
        PowerPlan? activePlan = null;
        _plans = _plans
            .Select(plan =>
            {
                var isActive = string.Equals(plan.Guid, activeGuid, StringComparison.OrdinalIgnoreCase);
                var updated = plan with { IsActive = isActive };
                if (isActive)
                {
                    activePlan = updated;
                }

                return updated;
            })
            .ToList();

        if (activePlan is not null)
        {
            return activePlan;
        }

        var nameMatch = Regex.Match(result.Output, @"\((.+?)\)");
        return nameMatch.Success ? new PowerPlan(nameMatch.Groups[1].Value, activeGuid, true) : null;
    }

    private static async Task<PowerCfgResult> RunPowerCfgAsync(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new PowerCfgResult(false, "");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new PowerCfgResult(false, "");
            }

            var output = await outputTask;
            _ = await errorTask;
            return new PowerCfgResult(process.ExitCode == 0, output);
        }
        catch
        {
            return new PowerCfgResult(false, "");
        }
    }

    private sealed record PowerCfgResult(bool Success, string Output);
}

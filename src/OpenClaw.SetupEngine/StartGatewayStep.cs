using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

public sealed class StartGatewayStep : SetupStep
{
    public override string Id => "start-gateway";
    public override string DisplayName => "Start gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
        StartOrRestartAndWaitForHealthAsync(ctx, restart: false, ct);

    internal static Task<StepResult> RestartAndWaitForHealthAsync(
        SetupContext ctx,
        CancellationToken ct) =>
        StartOrRestartAndWaitForHealthAsync(ctx, restart: true, ct);

    private static async Task<StepResult> StartOrRestartAndWaitForHealthAsync(
        SetupContext ctx,
        bool restart,
        CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var pathCmd = ctx.WslPathPrefix;
        var action = restart ? "restart" : "start";

        if (!restart)
        {
            var portCheck = await ctx.Commands.RunInWslAsync(
                distro, $"ss -H -ltnp 'sport = :{ctx.Config.GatewayPort}'",
                TimeSpan.FromSeconds(10), ct: ct);

            if (portCheck.ExitCode != 0)
                return StepResult.Fail($"Could not inspect gateway port {ctx.Config.GatewayPort} (exit {portCheck.ExitCode}).");

            if (!string.IsNullOrWhiteSpace(portCheck.Stdout))
            {
                // Installation may already start the service. Process names (including node) are not ownership proof.
                var service = await ctx.Commands.RunInWslAsync(
                    distro, "systemctl --user show openclaw-gateway.service -p MainPID --value",
                    TimeSpan.FromSeconds(10), ct: ct);
                var listeners = portCheck.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (service.ExitCode != 0 || !int.TryParse(service.Stdout.Trim(), out var pid) || pid <= 0 ||
                    listeners.Any(line => Regex.Matches(line, @"pid=(\d+),") is var owners &&
                        (owners.Count == 0 || owners.Any(owner => owner.Groups[1].Value != pid.ToString()))))
                {
                    var names = string.Join(", ", Regex.Matches(portCheck.Stdout, "\\(\\\"([^\\\"]+)\\\",")
                        .Select(owner => owner.Groups[1].Value).Distinct());
                    var ownerDetail = names.Length > 0 ? $" Owning process: {names}." : "";
                    ctx.Logger.Warn($"Port {ctx.Config.GatewayPort} is in use by another process.{ownerDetail}");
                    return StepResult.Fail(
                        $"Port {ctx.Config.GatewayPort} is already in use by another process.{ownerDetail} Either stop the conflicting process or change GatewayPort in the setup config.");
                }

                ctx.Logger.Info($"Port {ctx.Config.GatewayPort} is owned by openclaw-gateway.service (PID {pid}). Post-install port check succeeded.");
            }
        }

        var start = await ctx.Commands.RunInWslAsync(
            distro, $"{pathCmd} && openclaw gateway {action}", TimeSpan.FromSeconds(30), ct: ct);

        if (start.ExitCode != 0)
        {
            // Check if systemd start-limit-hit
            if (start.Stderr.Contains("start-limit", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.Warn("Start-limit hit, resetting and retrying");
                await ctx.Commands.RunInWslAsync(
                    distro,
                    "systemctl --user reset-failed openclaw-gateway.service",
                    TimeSpan.FromSeconds(10),
                    ct: ct);
                await Task.Delay(2000, ct);
                start = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"{pathCmd} && openclaw gateway {action}",
                    TimeSpan.FromSeconds(30),
                    ct: ct);
                if (start.ExitCode != 0)
                    return StepResult.Fail($"Gateway {action} failed after reset: {start.Stderr}");
            }
            else
            {
                return StepResult.Fail($"Gateway {action} failed (exit {start.ExitCode}): {start.Stderr}");
            }
        }

        return await WaitForHealthAsync(ctx, ct);
    }

    internal static async Task<StepResult> WaitForHealthAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        ctx.Logger.Info("Waiting for gateway health endpoint...");
        var healthDeadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(ctx.Config.Gateway.HealthTimeoutSeconds));

        while (DateTimeOffset.UtcNow < healthDeadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await ctx.Commands.RunInWslAsync(
                distro, "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:" + ctx.Config.GatewayPort + "/ --max-time 3",
                TimeSpan.FromSeconds(10), ct: ct);

            if (status.ExitCode == 0 && status.Stdout.Trim() is "200" or "401" or "403")
            {
                ctx.Logger.Info($"Gateway is accepting connections (HTTP {status.Stdout.Trim()})");
                return StepResult.Ok("Gateway running");
            }

            ctx.Logger.Debug($"Gateway not yet accepting connections (curl exit={status.ExitCode}, response={status.Stdout.Trim()})");

            await Task.Delay(2000, ct);
        }

        // Capture service status and journal for diagnostics
        var statusResult = await ctx.Commands.RunInWslAsync(
            distro,
            "systemctl --user status openclaw-gateway.service 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var journal = await ctx.Commands.RunInWslAsync(
            distro,
            "journalctl --user-unit openclaw-gateway.service --no-pager -n 30 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var redactedStatus = RedactTokens(statusResult.Stdout);
        var redactedJournal = RedactTokens(journal.Stdout);

        ctx.Logger.Error($"Gateway health timeout.\nService status:\n{redactedStatus}\nJournal:\n{redactedJournal}");

        return StepResult.Fail($"Gateway did not become healthy within {ctx.Config.Gateway.HealthTimeoutSeconds}s");
    }

    internal static string RedactTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[0-9a-fA-F]{32,}",
            m => m.Value[..8] + "…[REDACTED]");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Check if distro is running before trying systemctl stop
        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (!WslInstallSupport.ContainsDistro(list.Stdout, distro))
        {
            ctx.Logger.Info("[Uninstall] Distro not registered — skipping gateway stop");
            return;
        }

        // Check distro state — only stop if Running
        var verbose = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--verbose"], TimeSpan.FromSeconds(15), ct: ct);
        var isRunning = WslInstallSupport.Normalize(verbose.Stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(distro, StringComparison.OrdinalIgnoreCase)
                      && line.Contains("Running", StringComparison.OrdinalIgnoreCase));

        if (!isRunning)
        {
            ctx.Logger.Info("[Uninstall] Distro not running — skipping systemctl stop");
            return;
        }

        // Stop gateway service with 5-second timeout (mirrors old uninstall step 3)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await ctx.Commands.RunInWslAsync(
                distro, "bash -c 'systemctl --user stop openclaw-gateway 2>&1 || true'",
                TimeSpan.FromSeconds(10), ct: cts.Token);
            ctx.Logger.Info("[Uninstall] Stopped gateway service");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.Warn("[Uninstall] systemctl stop timed out (5s); distro may be wedged — wsl --unregister will force-terminate");
        }
    }
}

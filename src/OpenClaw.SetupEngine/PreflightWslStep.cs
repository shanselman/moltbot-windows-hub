using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal enum WslViabilityKind
{
    Ready,
    Installable,
    UpdateRequired,
    EnvironmentBlocked,
    InspectionFailed,
}

internal sealed record WslViabilityResult(
    WslViabilityKind Kind,
    string Summary,
    string Remediation)
{
    public bool BlocksSetup => Kind is
        WslViabilityKind.UpdateRequired or
        WslViabilityKind.EnvironmentBlocked or
        WslViabilityKind.InspectionFailed;

    public string Description => string.IsNullOrWhiteSpace(Remediation)
        ? Summary
        : $"{Summary} {Remediation}";
}

/// <summary>
/// Performs a read-only WSL inspection. This type never installs WSL, changes
/// optional Windows features, updates .wslconfig, or stops a distribution.
/// </summary>
internal static class WslViabilityInspector
{
    public static async Task<WslViabilityResult> InspectAsync(
        ICommandRunner commands,
        SetupLogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(logger);

        CommandResult versionResult;
        try
        {
            versionResult = await commands.RunAsync(
                WslConstants.WslExePath,
                ["--version"],
                TimeSpan.FromSeconds(5),
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn($"WSL version inspection failed: {ex.Message}");
            return InspectionFailed();
        }

        if (versionResult.ExitCode != 0)
        {
            if (LooksUnavailable(versionResult))
            {
                return new(
                    WslViabilityKind.Installable,
                    "WSL is not installed yet.",
                    "Setup can request administrator approval to install and verify it before downloading Local AI.");
            }

            if (LooksTooOldForVersionCommand(versionResult))
            {
                return new(
                    WslViabilityKind.UpdateRequired,
                    "The installed WSL version is too old for a clean app-owned gateway.",
                    WslInstallSupport.UpdateInstructions);
            }

            logger.Warn($"WSL version inspection returned exit code {versionResult.ExitCode}: " +
                NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}").Trim());
            return InspectionFailed();
        }

        var versionOutput = NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}");
        if (!WslInstallSupport.TryParseWslVersion(versionOutput, out var wslVersion))
        {
            return new(
                WslViabilityKind.UpdateRequired,
                "The installed WSL version could not be verified.",
                WslInstallSupport.UpdateInstructions);
        }

        if (!WslInstallSupport.SupportsDirectNamedInstall(wslVersion))
        {
            return new(
                WslViabilityKind.UpdateRequired,
                $"WSL {wslVersion} cannot create a clean app-owned OpenClaw gateway.",
                WslInstallSupport.UpdateInstructions);
        }

        logger.Info($"WSL version output: {NormalizeWslOutput(versionResult.Stdout).Trim()}");
        logger.Info($"WSL direct named install is supported (version {wslVersion})");

        CommandResult status;
        try
        {
            status = await commands.RunAsync(
                WslConstants.WslExePath,
                ["--status"],
                TimeSpan.FromSeconds(10),
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn($"WSL status inspection failed: {ex.Message}");
            return InspectionFailed();
        }

        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (LooksPlatformInstallRequired(status))
        {
            return new(
                WslViabilityKind.Installable,
                "WSL is not initialized yet.",
                "Setup can request administrator approval to initialize and verify it before continuing.");
        }

        if (WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
        {
            logger.Warn($"WSL environment issue detected: {NormalizeWslOutput(combined).Trim()}");
            return new(
                WslViabilityKind.EnvironmentBlocked,
                "Windows cannot currently start WSL2.",
                message);
        }

        if (status.ExitCode != 0 || status.TimedOut)
        {
            logger.Warn($"WSL status inspection returned exit code {status.ExitCode}: " +
                NormalizeWslOutput(combined).Trim());
            return InspectionFailed();
        }

        return new(
            WslViabilityKind.Ready,
            $"WSL {wslVersion} is ready.",
            string.Empty);
    }

    private static WslViabilityResult InspectionFailed() => new(
        WslViabilityKind.InspectionFailed,
        "OpenClaw could not safely verify the WSL2 environment.",
        "Run wsl --status in PowerShell, resolve the reported problem, and try setup again.");

    internal static bool LooksUnavailable(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Windows Subsystem for Linux has no installed distributions", StringComparison.OrdinalIgnoreCase)
            || LooksPlatformInstallRequired(text)
            || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksPlatformInstallRequired(CommandResult result) =>
        LooksPlatformInstallRequired(NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}"));

    private static bool LooksPlatformInstallRequired(string text) =>
        text.Contains("WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED", StringComparison.OrdinalIgnoreCase)
        || text.Contains("0x8007019e", StringComparison.OrdinalIgnoreCase)
        || text.Contains("requires the Windows Subsystem for Linux Optional Component", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Optional components needed to run WSL are not installed", StringComparison.OrdinalIgnoreCase);

    private static bool LooksTooOldForVersionCommand(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("Invalid command line option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown option", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeWslOutput(string value) => WslInstallSupport.Normalize(value);
}

public sealed class PreflightWslStep : SetupStep
{
    public override string Id => "preflight-wsl";
    public override string DisplayName => "Inspect WSL compatibility";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        WslViabilityResult viability = await WslViabilityInspector.InspectAsync(
            ctx.Commands,
            ctx.Logger,
            ct);
        ctx.WslViability = viability;
        return viability.BlocksSetup
            ? StepResult.Terminal(viability.Description)
            : StepResult.Ok(viability.Description);
    }

    internal static async Task<string?> DetectEnvironmentIssueAsync(SetupContext ctx, CancellationToken ct)
    {
        var status = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--status"],
            TimeSpan.FromSeconds(10),
            ct: ct);
        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (!WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
            return null;

        ctx.Logger.Warn($"WSL environment issue detected: {WslViabilityInspector.NormalizeWslOutput(combined).Trim()}");
        return message;
    }

    internal static async Task<StepResult> InstallWslPlatformAsync(SetupContext ctx, CancellationToken ct)
    {
        ctx.Logger.Warn("WSL platform appears to be missing; launching elevated WSL platform install");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WslConstants.WslExePath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WorkingDirectory = WslConstants.SafeWindowsWorkingDirectory
            };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--no-distribution");

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Could not start elevated WSL platform installer.");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 3010)
                return StepResult.RestartRequired("WSL platform install requires a restart. Reboot Windows, then run setup again.");

            if (process.ExitCode != 0)
            {
                GitHubApiQuota? quota = await WslPlatformInstallDiagnostics.QueryGitHubQuotaAsync(ct);
                return StepResult.Fail(WslPlatformInstallDiagnostics.DescribeFailure(process.ExitCode, quota));
            }

            var probe = await ctx.Commands.RunAsync(
                WslConstants.WslExePath,
                ["--version"],
                TimeSpan.FromSeconds(5),
                ct: ct);
            if (probe.ExitCode != 0 || WslViabilityInspector.LooksUnavailable(probe))
            {
                return StepResult.RestartRequired(
                    "WSL platform install completed, but Windows still reports WSL unavailable. Reboot Windows, then run setup again.");
            }

            return StepResult.Ok("WSL platform installed");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return StepResult.Fail("WSL platform install was cancelled at the elevation prompt.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"WSL platform install failed: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Performs the first WSL mutation after read-only hardware and WSL inspection,
/// before Local AI downloads begin.
/// </summary>
public sealed class EnsureWslPlatformStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _installer;

    public EnsureWslPlatformStep()
        : this(PreflightWslStep.InstallWslPlatformAsync)
    {
    }

    internal EnsureWslPlatformStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> installer) =>
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));

    public override string Id => "ensure-wsl-platform";
    public override string DisplayName => "Prepare WSL platform";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        WslViabilityResult viability = ctx.WslViability
            ?? await WslViabilityInspector.InspectAsync(ctx.Commands, ctx.Logger, ct);
        ctx.WslViability = viability;

        if (viability.Kind == WslViabilityKind.Ready)
            return StepResult.Ok("WSL platform is ready.");
        if (viability.BlocksSetup)
            return StepResult.Terminal(viability.Description);

        StepResult install = await _installer(ctx, ct);
        if (!install.IsSuccess)
            return install;

        viability = await WslViabilityInspector.InspectAsync(ctx.Commands, ctx.Logger, ct);
        ctx.WslViability = viability;
        if (viability.Kind == WslViabilityKind.Ready)
            return StepResult.Ok("WSL platform installed and verified.");
        if (viability.Kind is WslViabilityKind.Installable or WslViabilityKind.EnvironmentBlocked)
        {
            return StepResult.RestartRequired(
                "WSL platform installation completed, but Windows must be restarted before WSL is ready. " +
                "Reboot Windows, then run setup again.");
        }

        return StepResult.Terminal(viability.Description);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

// ═══════════════════════════════════════════════════════════════════
// WSL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class CreateWslInstanceStep : SetupStep
{
    private static readonly TimeSpan DistroVersionVerificationTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] FreshDistroProbeTimeouts =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(90),
    ];

    public override string Id => "wsl-create";
    public override string DisplayName => "Create WSL instance";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var baseDistro = ctx.Config.BaseDistro.Trim();

        if (string.IsNullOrWhiteSpace(baseDistro))
            return StepResult.Terminal("BaseDistro is required for fresh WSL gateway setup.");

        if (!DistroInstallPathPolicy.TryGetNewInstallPath(ctx.LocalDataDir, distro, out var installPath, out var pathError))
            return StepResult.Terminal(pathError);

        ctx.Logger.Info($"Creating clean app-owned WSL distro '{distro}' from '{baseDistro}' at '{installPath}'");

        var existing = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct,
            allowInheritedPipeHandleEscape: true);
        if (existing.ExitCode != 0)
            return StepResult.Fail($"Failed to list WSL distros before creating '{distro}': {existing.Stderr}");

        if (WslInstallSupport.ContainsDistro(existing.Stdout, distro))
            return StepResult.Fail($"Target WSL distro '{distro}' still exists after cleanup; refusing to create a new gateway over unknown state.");

        var pathCheck = EnsureInstallPathReady(installPath);
        if (!pathCheck.IsSuccess)
            return pathCheck;

        try
        {
            await ManagedDistroOwnership.WriteMarkerAsync(
                ctx.LocalDataDir,
                distro,
                installPath,
                ct);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return StepResult.Fail(
                $"OpenClaw could not record ownership before creating WSL distro '{distro}': {ex.Message}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);

        var installArgs = WslInstallSupport.BuildDirectInstallArgs(baseDistro, distro, installPath);
        ctx.Logger.Info($"Installing fresh WSL distro with arguments: {string.Join(' ', installArgs)}");
        var install = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            installArgs,
            TimeSpan.FromMinutes(15),
            ct: ct);

        if (install.ExitCode != 0)
        {
            var cleanupError = await CleanupPartialInstall(ctx, distro, installPath, ct);
            return StepResult.Fail(
                $"Fresh WSL install failed for '{distro}' from '{baseDistro}' (exit {install.ExitCode}): {FirstNonEmpty(install.Stderr, install.Stdout)}{cleanupError}");
        }

        var verify = await VerifyFreshDistro(ctx, distro, installPath, ct);
        if (!verify.IsSuccess)
        {
            var cleanupError = await CleanupPartialInstall(ctx, distro, installPath, ct);
            return StepResult.Fail($"{verify.Message}{cleanupError}");
        }

        return verify;
    }

    private static StepResult EnsureInstallPathReady(string installPath)
    {
        if (File.Exists(installPath))
        {
            if (File.GetAttributes(installPath).HasFlag(FileAttributes.ReparsePoint))
                return StepResult.Fail($"App-owned WSL install path '{installPath}' is a reparse point; remove it manually and retry setup.");

            File.Delete(installPath);
            return StepResult.Ok();
        }

        if (!Directory.Exists(installPath))
            return StepResult.Ok();

        if (new DirectoryInfo(installPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return StepResult.Fail($"App-owned WSL install directory '{installPath}' is a reparse point; remove it manually and retry setup.");

        if (Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            return StepResult.Fail(
                $"App-owned WSL install directory '{installPath}' still contains files after cleanup; refusing to create a new gateway over unknown state.");
        }

        Directory.Delete(installPath);
        return StepResult.Ok();
    }

    private static async Task<StepResult> VerifyFreshDistro(SetupContext ctx, string distro, string installPath, CancellationToken ct)
    {
        var list = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct,
            allowInheritedPipeHandleEscape: true);
        if (list.ExitCode != 0 || !WslInstallSupport.ContainsDistro(list.Stdout, distro))
        {
            var environmentIssue = await PreflightWslStep.DetectEnvironmentIssueAsync(ctx, ct);
            var baseMessage = $"Fresh WSL install did not register expected distro '{distro}'.";
            return StepResult.Fail(environmentIssue != null ? $"{baseMessage} {environmentIssue}" : baseMessage);
        }

        var verbose = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--verbose"],
            DistroVersionVerificationTimeout,
            ct: ct,
            allowInheritedPipeHandleEscape: true);
        if (verbose.ExitCode != 0 || !WslInstallSupport.TryGetDistroVersion(verbose.Stdout, distro, out var version))
            return StepResult.Fail($"Fresh WSL install registered '{distro}', but setup could not verify it is WSL2.");

        if (version != 2)
            return StepResult.Fail($"Fresh WSL install registered '{distro}' as WSL{version}; WSL2 is required.");

        CommandResult? probe = null;
        for (var attempt = 0; attempt < FreshDistroProbeTimeouts.Length; attempt++)
        {
            probe = await ctx.Commands.RunAsync(
                WslConstants.WslExePath,
                ["-d", distro, "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"],
                FreshDistroProbeTimeouts[attempt],
                ct: ct);
            if (probe.ExitCode == 0
                && probe.Stdout.Contains("OPENCLAW_FRESH_WSL_READY", StringComparison.Ordinal))
            {
                break;
            }

            if (attempt < FreshDistroProbeTimeouts.Length - 1)
            {
                ctx.Logger.Warn(
                    $"Fresh WSL distro '{distro}' root probe was not ready " +
                    $"(attempt {attempt + 1}/{FreshDistroProbeTimeouts.Length}); retrying.");
            }
        }

        if (probe is null
            || probe.ExitCode != 0
            || !probe.Stdout.Contains("OPENCLAW_FRESH_WSL_READY", StringComparison.Ordinal))
        {
            var detail = probe is null ? "no output" : FirstNonEmpty(probe.Stderr, probe.Stdout);
            return StepResult.Fail($"Fresh WSL distro '{distro}' could not run a root verification command: {detail}");
        }

        return StepResult.Ok($"Created clean WSL2 distro '{distro}' at '{installPath}'");
    }

    private static async Task<string> CleanupPartialInstall(SetupContext ctx, string distro, string installPath, CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        var installPathExists = Directory.Exists(installPath) || File.Exists(installPath);
        var list = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct,
            allowInheritedPipeHandleEscape: true);
        var registrationStateKnown = list.ExitCode == 0;
        var distroExists = registrationStateKnown && WslInstallSupport.ContainsDistro(list.Stdout, distro);
        var canDeleteInstallPath = registrationStateKnown && !distroExists;

        if (!registrationStateKnown)
        {
            ctx.Logger.Warn($"Partial install cleanup could not list WSL distros (exit {list.ExitCode}); attempting best-effort unregister for '{distro}' before deleting app-owned files");
            canDeleteInstallPath = await TryUnregisterPartialInstall(ctx, distro, cleanupErrors, ct);
        }
        else if (distroExists)
        {
            canDeleteInstallPath = await TryUnregisterPartialInstall(ctx, distro, cleanupErrors, ct);
        }

        if (!canDeleteInstallPath)
        {
            if (!registrationStateKnown)
            {
                cleanupErrors.Insert(0,
                    $"could not confirm whether distro '{distro}' is still registered: {FirstNonEmpty(list.Stderr, list.Stdout)}");
            }

            if (installPathExists)
            {
                cleanupErrors.Add(
                    $"skipped deleting app-owned install path '{installPath}' until distro '{distro}' is confirmed unregistered");
            }
        }
        else if (installPathExists)
        {
            var delete = await CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(ctx, distro, installPath, ct);
            if (!delete.IsSuccess)
                cleanupErrors.Add(delete.Message ?? "install directory cleanup failed");
        }

        if (cleanupErrors.Count == 0)
        {
            ManagedDistroOwnership.DeleteMarker(
                ctx.LocalDataDir,
                distro,
                installPath);
            return "";
        }

        return $" Partial app-owned distro cleanup also failed: {string.Join("; ", cleanupErrors)}";
    }

    private static async Task<bool> TryUnregisterPartialInstall(SetupContext ctx, string distro, List<string> cleanupErrors, CancellationToken ct)
    {
        var terminate = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        if (terminate.ExitCode != 0 && !IsMissingDistroResult(terminate))
            ctx.Logger.Warn($"Targeted terminate for '{distro}' failed before unregister (exit {terminate.ExitCode}): {FirstNonEmpty(terminate.Stderr, terminate.Stdout)}");

        var unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode == 0 || IsMissingDistroResult(unregister))
            return true;

        ctx.Logger.Warn($"Partial install unregister failed (exit {unregister.ExitCode}); retrying targeted termination");
        terminate = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        if (terminate.ExitCode != 0 && !IsMissingDistroResult(terminate))
            ctx.Logger.Warn($"Targeted terminate retry for '{distro}' failed (exit {terminate.ExitCode}): {FirstNonEmpty(terminate.Stderr, terminate.Stdout)}");

        unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode == 0 || IsMissingDistroResult(unregister))
            return true;

        cleanupErrors.Add($"unregister exit {unregister.ExitCode}: {FirstNonEmpty(unregister.Stderr, unregister.Stdout)}");
        return false;
    }

    private static bool IsMissingDistroResult(CommandResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = FirstNonEmpty(result.Stderr, result.Stdout);
        return output.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? "no output";

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(ctx.LocalDataDir, distro, out var vhdDir, out var pathError))
            throw new IOException($"[Uninstall] Refusing WSL rollback filesystem cleanup: {pathError}");

        var cleanupError = await CleanupPartialInstall(ctx, distro, vhdDir, ct);
        if (cleanupError.Length > 0)
            throw new IOException($"[Uninstall] Refusing unsafe WSL rollback cleanup.{cleanupError}");

        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                ctx.LocalDataDir,
                distro,
                out var revalidatedPath,
                out pathError))
        {
            throw new IOException($"[Uninstall] Refusing WSL parent cleanup: {pathError}");
        }

        var wslDir = Path.GetDirectoryName(revalidatedPath)!;
        if (Directory.Exists(wslDir) &&
            !new DirectoryInfo(wslDir).Attributes.HasFlag(FileAttributes.ReparsePoint) &&
            !Directory.EnumerateFileSystemEntries(wslDir).Any())
        {
            Directory.Delete(wslDir);
            ctx.Logger.Info("[Uninstall] Deleted empty wsl\\ parent directory");
        }
    }
}

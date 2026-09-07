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
// GATEWAY INSTALL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class InstallCliStep : SetupStep
{
    internal const int DownloadMaxTimeSeconds = 60;
    internal static readonly TimeSpan InstallerCommandTimeout = TimeSpan.FromMinutes(5);
    internal const string InstallerTempDirectoryPreview =
        "/tmp/openclaw-installer-<32-hex-random>";
    internal const string StagedValidationPackageReference =
        "file:/var/lib/openclaw/setup-package/openclaw-current.tgz";
    private const string InstallerTempDirectoryPrefix = "/tmp/openclaw-installer-";
    private const string InstallerTempDirectoryPreviewSource =
        "/tmp/openclaw-installer-00000000000000000000000000000000";
    private const string StagedValidationPackageDirectory = "/var/lib/openclaw/setup-package";

    public override string Id => "install-cli";
    public override string DisplayName => "Install OpenClaw CLI";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(5));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var user = ctx.Config.Wsl.User;
        var installVersion = ctx.Config.Gateway.Version;
        var validationPackageStaged = false;

        // Download and run install script (URL configurable)
        var installUrl = ctx.Config.Gateway.InstallUrl ?? GatewayReleasePolicy.DefaultInstallUrl;

        // Validate URL is HTTPS to prevent downgrade attacks
        if (!Uri.TryCreate(installUrl, UriKind.Absolute, out var parsedUrl) ||
            !string.Equals(parsedUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return StepResult.Fail($"Installer URL must be HTTPS: {installUrl}");
        }

        var officialInstaller = GatewayReleasePolicy.IsOfficialInstallerUrl(installUrl);
        if (ctx.Config.Gateway.ValidationPackagePath is { } validationPackagePath)
        {
            var stageResult = await StageValidationPackageAsync(
                ctx,
                distro,
                validationPackagePath,
                ct);
            if (!stageResult.IsSuccess)
                return stageResult;

            installVersion = StagedValidationPackageReference;
            validationPackageStaged = true;
        }

        try
        {
            string installScript;
            var installerTempDirectory =
                $"/tmp/openclaw-installer-{Guid.NewGuid():N}";
            try
            {
                installScript = BuildInstallCommand(
                    installUrl,
                    installVersion,
                    officialInstaller ? GatewayReleasePolicy.NodeVersion : null,
                    installerTempDirectory);
            }
            catch (ArgumentException ex)
            {
                return StepResult.Fail(ex.Message);
            }

            CommandResult result;
            string? cleanupError = null;
            try
            {
                result = await ctx.Commands.RunInWslAsync(
                    distro,
                    installScript,
                    InstallerCommandTimeout,
                    ct: ct,
                    inputViaStdin: true);
            }
            finally
            {
                cleanupError = await CleanupInstallerTempDirectoryAsync(
                    ctx,
                    distro,
                    installerTempDirectory);
            }

            if (result.TimedOut)
            {
                var stderr = string.IsNullOrWhiteSpace(result.Stderr) ? "" : $" {result.Stderr.Trim()}";
                var cleanup = FormatCleanupError(cleanupError);
                return StepResult.Fail(
                    $"CLI installer command timed out after {InstallerCommandTimeout.TotalMinutes:0} minutes.{stderr}{cleanup}");
            }

            if (result.ExitCode != 0)
            {
                var diagnostic = string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stdout.Trim()
                    : result.Stderr.Trim();
                var cleanup = FormatCleanupError(cleanupError);
                return StepResult.Fail(
                    $"CLI install failed (exit {result.ExitCode}): {diagnostic}{cleanup}");
            }

            if (cleanupError is not null)
                return StepResult.Fail($"CLI installer cleanup failed: {cleanupError}");

            var verifyCommands = new (string Command, string? ExecutablePath)[]
            {
                ("openclaw --version", null),
                ($"/home/{user}/.openclaw/bin/openclaw --version", $"/home/{user}/.openclaw/bin/openclaw"),
                ("/opt/openclaw/bin/openclaw --version", "/opt/openclaw/bin/openclaw"),
                ("/usr/local/bin/openclaw --version", "/usr/local/bin/openclaw")
            };

            foreach (var (cmd, executablePath) in verifyCommands)
            {
                var verify = await ctx.Commands.RunInWslAsync(distro, cmd, TimeSpan.FromSeconds(15), ct: ct);
                if (verify.ExitCode == 0 && !string.IsNullOrWhiteSpace(verify.Stdout))
                {
                    var selectedVersion = ctx.Config.Gateway.Version!;
                    if (!GatewayReleaseVersion.TryExtract(verify.Stdout, out var installedVersion) ||
                        !string.Equals(installedVersion, selectedVersion, StringComparison.Ordinal))
                    {
                        var actual = string.IsNullOrWhiteSpace(installedVersion) ? "unparseable" : installedVersion;
                        var failure = new GatewayCompatibilityException(
                            GatewayCompatibilityFailureKind.InstalledVersionMismatch,
                            $"Gateway compatibility check failed: selected version {selectedVersion}, installed CLI reported {actual}.");
                        return StepResult.Terminal(failure.Message, failure);
                    }

                    if (executablePath != null)
                    {
                        var pathResult = await EnsureCliOnDefaultPathAsync(ctx, distro, executablePath, ct);
                        if (!pathResult.IsSuccess)
                            return pathResult;
                    }

                    if (officialInstaller)
                    {
                        var expectedRuntimeVersion = $"v{GatewayReleasePolicy.NodeVersion}";
                        var runtimeCommand = $"/home/{user}/.openclaw/tools/node/bin/node --version";
                        var runtime = await ctx.Commands.RunInWslAsync(
                            distro,
                            runtimeCommand,
                            TimeSpan.FromSeconds(15),
                            ct: ct);
                        var actualRuntimeVersion = runtime.Stdout.Trim();
                        if (runtime.ExitCode != 0 ||
                            !string.Equals(actualRuntimeVersion, expectedRuntimeVersion, StringComparison.Ordinal))
                        {
                            var actual = string.IsNullOrWhiteSpace(actualRuntimeVersion)
                                ? "missing"
                                : actualRuntimeVersion;
                            var failure = new GatewayCompatibilityException(
                                GatewayCompatibilityFailureKind.InstalledRuntimeMismatch,
                                $"Gateway compatibility check failed: selected Node runtime {expectedRuntimeVersion}, installed runtime reported {actual}.");
                            return StepResult.Terminal(failure.Message, failure);
                        }

                        ctx.Logger.Info($"Gateway Node runtime: {actualRuntimeVersion}");
                    }

                    ctx.Logger.Info($"OpenClaw CLI version: {verify.Stdout.Trim()}");
                    return StepResult.Ok($"CLI installed: {verify.Stdout.Trim()}");
                }
            }

            return StepResult.Fail("CLI installed but not found in any known location");
        }
        finally
        {
            if (validationPackageStaged)
                await CleanupStagedValidationPackageAsync(ctx, distro);
        }
    }

    internal static string BuildInstallCommand(
        string installUrl,
        string? requestedVersion,
        string? nodeVersion = null,
        string? installerTempDirectory = null)
    {
        var escapedUrl = WslShellQuoting.EscapePosixSingleQuoteInner(installUrl);
        if (string.IsNullOrWhiteSpace(requestedVersion))
            throw new ArgumentException("Gateway release policy must resolve an exact version before installation.");

        var trimmedVersion = requestedVersion.Trim();
        if (trimmedVersion.Contains('\n') || trimmedVersion.Contains('\r'))
            throw new ArgumentException("Gateway version cannot contain newlines.");

        var escapedVersion = WslShellQuoting.EscapePosixSingleQuoteInner(trimmedVersion);
        var runtimeArgument = "";
        if (!string.IsNullOrWhiteSpace(nodeVersion))
        {
            var trimmedNodeVersion = nodeVersion.Trim();
            if (!Version.TryParse(trimmedNodeVersion, out _))
                throw new ArgumentException("Gateway Node runtime must be an exact numeric version.");

            var escapedNodeVersion = WslShellQuoting.EscapePosixSingleQuoteInner(trimmedNodeVersion);
            runtimeArgument = $" --node-version '{escapedNodeVersion}'";
        }
        var transferDeadlineArguments = GatewayReleasePolicy.IsOfficialInstallerUrl(installUrl)
            ? $" --connect-timeout 15 --max-time {DownloadMaxTimeSeconds}"
            : "";

        installerTempDirectory ??= $"{InstallerTempDirectoryPrefix}{Guid.NewGuid():N}";
        if (!installerTempDirectory.StartsWith(InstallerTempDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("CLI installer temporary directory is invalid.");
        }

        var suffix = installerTempDirectory[InstallerTempDirectoryPrefix.Length..];
        if (suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("CLI installer temporary directory is invalid.");

        return $"""
            set -euo pipefail
            umask 077
            installer_dir='{installerTempDirectory}'
            mkdir -m 0700 -- "$installer_dir"
            installer="$installer_dir/installer.sh"
            trap 'rm -rf -- "$installer_dir"' EXIT
            curl -fsSL{transferDeadlineArguments} \
              --proto '=https' \
              --tlsv1.2 \
              --output "$installer" \
              '{escapedUrl}'
            if ! test -s "$installer"; then
              echo 'CLI installer download was empty.' >&2
              exit 65
            fi
            bash -s -- --version '{escapedVersion}'{runtimeArgument} < "$installer"
            """;
    }

    internal static string BuildInstallCommandPreview(
        string installUrl,
        string requestedVersion,
        string? nodeVersion = null)
    {
        var command = BuildInstallCommand(
            installUrl,
            requestedVersion,
            nodeVersion,
            InstallerTempDirectoryPreviewSource);
        var assignment = $"installer_dir='{InstallerTempDirectoryPreviewSource}'";
        var assignmentIndex = command.IndexOf(assignment, StringComparison.Ordinal);
        if (assignmentIndex < 0)
            throw new InvalidOperationException("CLI installer preview could not locate the temporary directory.");

        return string.Concat(
            command.AsSpan(0, assignmentIndex),
            $"installer_dir='{InstallerTempDirectoryPreview}'",
            command.AsSpan(assignmentIndex + assignment.Length));
    }

    private static async Task<string?> CleanupInstallerTempDirectoryAsync(
        SetupContext ctx,
        string distro,
        string installerTempDirectory)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var cleanup = await ctx.Commands.RunInWslAsync(
                distro,
                $"rm -rf -- {installerTempDirectory}",
                TimeSpan.FromSeconds(15),
                ct: cleanupCts.Token);
            return cleanup.ExitCode == 0 && !cleanup.TimedOut
                ? null
                : string.IsNullOrWhiteSpace(cleanup.Stderr)
                    ? $"exit {cleanup.ExitCode}"
                    : $"exit {cleanup.ExitCode}: {cleanup.Stderr.Trim()}";
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            return "timed out after 15 seconds";
        }
        catch (OperationCanceledException ex)
        {
            ctx.Logger.Warn($"CLI installer cleanup was cancelled ({ex.GetType().Name}).");
            return "was cancelled";
        }
        catch (Exception ex) when (
            ex is IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            ctx.Logger.Warn($"CLI installer cleanup failed ({ex.GetType().Name}).");
            return $"failed ({ex.GetType().Name})";
        }
    }

    private static string FormatCleanupError(string? cleanupError)
        => cleanupError is null ? "" : $" Cleanup also failed: {cleanupError}";

    internal static bool TryValidateCandidatePackagePath(
        string candidatePackagePath,
        out string normalizedPath,
        out string? error)
    {
        normalizedPath = "";
        error = null;

        if (!Path.IsPathFullyQualified(candidatePackagePath) ||
            !candidatePackagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            error = "Gateway candidate package must name an absolute Windows .tgz file.";
            return false;
        }

        var root = Path.GetPathRoot(candidatePackagePath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            error = "Gateway candidate package must be on a local Windows drive.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(candidatePackagePath);
            if (!File.Exists(normalizedPath))
            {
                error = "Gateway candidate package does not exist.";
                return false;
            }

            if (new FileInfo(normalizedPath).Length == 0)
            {
                error = "Gateway candidate package must not be empty.";
                return false;
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            normalizedPath = "";
            error = $"Gateway candidate package could not be read: {ex.Message}";
            return false;
        }

        return true;
    }

    private static async Task<StepResult> StageValidationPackageAsync(
        SetupContext ctx,
        string distro,
        string candidatePackagePath,
        CancellationToken ct)
    {
        if (!TryValidateCandidatePackagePath(candidatePackagePath, out var sourcePath, out var validationError))
            return StepResult.Fail(validationError ?? "Gateway candidate package is invalid.");

        var prepare = await ctx.Commands.RunInWslAsync(
            distro,
            $"install -d -m 0755 {StagedValidationPackageDirectory}",
            TimeSpan.FromSeconds(30),
            ct: ct,
            user: "root");
        if (prepare.ExitCode != 0)
            return StepResult.Fail($"Could not prepare gateway candidate staging directory: {prepare.Stderr}");

        var stagedSuccessfully = false;
        try
        {
            var stagedPath = StagedValidationPackageReference["file:".Length..];
            var sourceHash = ComputeSha256(sourcePath);
            await using var source = File.OpenRead(sourcePath);
            var copy = await ctx.Commands.RunAsync(
                WslConstants.WslExePath,
                [
                    "-d", distro,
                    "-u", "root",
                    "--", "bash", "-c",
                    $"set -e; cat > {stagedPath}; chmod 0644 {stagedPath}; sha256sum {stagedPath} | cut -d ' ' -f1"
                ],
                TimeSpan.FromMinutes(2),
                ct: ct,
                stdinStream: source);

            if (copy.ExitCode != 0)
                return StepResult.Fail($"Could not copy gateway candidate package into WSL: {copy.Stderr}");

            if (!string.Equals(sourceHash, copy.Stdout.Trim(), StringComparison.OrdinalIgnoreCase))
                return StepResult.Fail("Gateway candidate package changed while it was copied into WSL.");

            ctx.Logger.Info("Copied verified gateway candidate package into the isolated WSL instance.");
            stagedSuccessfully = true;
            return StepResult.Ok();
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException)
        {
            return StepResult.Fail($"Could not copy gateway candidate package into WSL: {ex.Message}");
        }
        finally
        {
            if (!stagedSuccessfully)
                await CleanupStagedValidationPackageAsync(ctx, distro);
        }
    }

    private static async Task CleanupStagedValidationPackageAsync(
        SetupContext ctx,
        string distro)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var cleanup = await ctx.Commands.RunInWslAsync(
                distro,
                $"rm -rf -- {StagedValidationPackageDirectory}",
                TimeSpan.FromSeconds(15),
                ct: cleanupCts.Token,
                user: "root");
            if (cleanup.ExitCode != 0)
                ctx.Logger.Warn($"Could not remove staged gateway candidate package: {cleanup.Stderr}");
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            ctx.Logger.Warn("Timed out removing staged gateway candidate package.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<StepResult> EnsureCliOnDefaultPathAsync(
        SetupContext ctx,
        string distro,
        string executablePath,
        CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;

        if (!executablePath.StartsWith("/", StringComparison.Ordinal) ||
            executablePath.Contains('\'') ||
            executablePath.Contains('\n'))
        {
            return StepResult.Fail($"Refusing to create openclaw PATH symlink for unexpected install path: {executablePath}");
        }

        if (!string.Equals(executablePath, "/usr/local/bin/openclaw", StringComparison.Ordinal))
        {
            var linkCommand = $"""
                set -e
                ln -sfn {executablePath} /usr/local/bin/openclaw
                echo OPENCLAW_PATH_READY
                """;

            var link = await ctx.Commands.RunInWslAsync(
                distro,
                linkCommand,
                TimeSpan.FromSeconds(15),
                ct: ct,
                user: "root");

            if (link.ExitCode != 0 || !link.Stdout.Contains("OPENCLAW_PATH_READY", StringComparison.Ordinal))
                return StepResult.Fail($"Failed to make openclaw available on default PATH: {link.Stderr}");
        }

        var bareVerify = await ctx.Commands.RunInWslAsync(
            distro,
            $"env -i HOME=/home/{user} USER={user} PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin openclaw --version",
            TimeSpan.FromSeconds(15),
            ct: ct);

        if (bareVerify.ExitCode != 0 || string.IsNullOrWhiteSpace(bareVerify.Stdout))
            return StepResult.Fail($"openclaw PATH symlink verification failed: {bareVerify.Stderr}");

        ctx.Logger.Info($"OpenClaw CLI available on default PATH: {bareVerify.Stdout.Trim()}");
        return StepResult.Ok();
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"rm -rf /opt/openclaw /home/{user}/.openclaw /usr/local/bin/openclaw", TimeSpan.FromSeconds(30), ct: ct, user: "root");
    }
}

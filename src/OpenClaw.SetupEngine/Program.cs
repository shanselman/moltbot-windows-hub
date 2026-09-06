using System.Runtime.Versioning;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

[SupportedOSPlatform("windows")]
public static class Program
{
    internal static IReadOnlyList<string> ValueOptionNames { get; } = Array.AsReadOnly(
    [
        "--config",
        "--log-path",
        "--json-output",
        "--data-dir",
        "--local-data-dir",
        "--distro-name",
        "--gateway-port",
        "--gateway-candidate-package",
        "--tailscale-auth",
        "--tailscale-hostname",
        "--autostart-name",
        "--startup-task-name",
    ]);

    internal static IReadOnlyList<string> FlagOptionNames { get; } = Array.AsReadOnly(
    [
        "--headless",
        "--rollback-on-failure",
        "--no-rollback-on-failure",
        "--dry-run",
        "--wizard-only",
        "--uninstall",
        "--confirm-destructive",
        "--preserve-logs",
        "--tailscale",
        "--tailscale-trust-auth",
        "--validate-gateway-candidate",
    ]);

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"OpenClaw Setup Engine {AppVersionInfo.DisplayVersion}");
        Console.WriteLine("─────────────────────────────");

        if (!TryParseArguments(args, out var parsedArguments, out var argumentError))
        {
            Console.Error.WriteLine($"ERROR: {argumentError}");
            return 2;
        }

        var configPath = parsedArguments.GetValue("--config");
        var logPath = parsedArguments.GetValue("--log-path");
        var headless = parsedArguments.HasFlag("--headless");
        var rollback = parsedArguments.HasFlag("--rollback-on-failure");
        var noRollback = parsedArguments.HasFlag("--no-rollback-on-failure");
        var dryRun = parsedArguments.HasFlag("--dry-run");
        var wizardOnly = parsedArguments.HasFlag("--wizard-only");
        var uninstall = parsedArguments.HasFlag("--uninstall");
        var confirmDestructive = parsedArguments.HasFlag("--confirm-destructive");
        var jsonOutput = parsedArguments.GetValue("--json-output");
        var preserveLogs = parsedArguments.HasFlag("--preserve-logs");
        var dataDir = parsedArguments.GetValue("--data-dir");
        var localDataDir = parsedArguments.GetValue("--local-data-dir");
        var distroName = parsedArguments.GetValue("--distro-name");
        var gatewayPortText = parsedArguments.GetValue("--gateway-port");
        var gatewayCandidatePackage = parsedArguments.GetValue("--gateway-candidate-package");
        var tailscale = parsedArguments.HasFlag("--tailscale");
        var tailscaleTrustAuth = parsedArguments.HasFlag("--tailscale-trust-auth");
        var validateGatewayCandidate = parsedArguments.HasFlag("--validate-gateway-candidate");
        var tailscaleAuth = parsedArguments.GetValue("--tailscale-auth");
        var tailscaleHostname = parsedArguments.GetValue("--tailscale-hostname");
        var autoStartName = parsedArguments.GetValue("--autostart-name") ?? "OpenClawTray";
        var startupTaskName = parsedArguments.GetValue("--startup-task-name") ?? WindowsStartupTaskRegistration.TaskName;

        // Load config
        SetupConfig config;
        if (configPath != null)
        {
            Console.WriteLine($"Loading config from: {configPath}");
            if (!TryLoadConfig(configPath, out config, out var configError))
            {
                Console.Error.WriteLine($"ERROR: Cannot load config '{configPath}': {configError}");
                return 2;
            }
        }
        else
        {
            // Look for default-config.json next to the exe
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
            {
                Console.WriteLine($"Loading config from: {defaultPath}");
                if (!TryLoadConfig(defaultPath, out config, out var configError))
                {
                    Console.Error.WriteLine($"ERROR: Cannot load bundled config '{defaultPath}': {configError}");
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine("ERROR: No config file found. Provide --config or place default-config.json next to the exe.");
                return 1;
            }
        }

        // Apply CLI overrides
        config = SetupConfig.FromEnvironment(config);
        if (!string.IsNullOrWhiteSpace(distroName))
            config.DistroName = distroName;
        if (!string.IsNullOrWhiteSpace(gatewayPortText))
        {
            if (!int.TryParse(gatewayPortText, out var gatewayPort) || gatewayPort is <= 0 or > 65535)
            {
                Console.Error.WriteLine($"ERROR: Invalid --gateway-port value '{gatewayPortText}'.");
                return 2;
            }
            config.GatewayPort = gatewayPort;
            config.GatewayUrl = null;
        }
        if (tailscale)
            config.Tailscale.Enabled = true;
        if (tailscaleTrustAuth)
        {
            config.Tailscale.Enabled = true;
            config.Tailscale.TrustTailscaleAuth = true;
        }
        if (!string.IsNullOrWhiteSpace(tailscaleAuth))
        {
            if (!TailscaleConfig.TryParseAuthMode(tailscaleAuth, out var authMode))
            {
                Console.Error.WriteLine($"ERROR: Invalid --tailscale-auth value '{tailscaleAuth}'. Use browser or auth-key.");
                return 2;
            }
            config.Tailscale.AuthMode = authMode;
        }
        if (!string.IsNullOrWhiteSpace(tailscaleHostname))
            config.Tailscale.Hostname = tailscaleHostname;
        if (headless) config.Headless = true;
        if (rollback) config.RollbackOnFailure = true;
        if (noRollback) config.RollbackOnFailure = false;
        if (wizardOnly) config.SkipWizard = false;
        if (logPath != null) config.LogPath = logPath;
        if (dryRun) config.DryRun = true;
        if (confirmDestructive) config.ConfirmDestructive = true;

        if (gatewayCandidatePackage != null)
        {
            if (!validateGatewayCandidate || !config.Headless || !config.RollbackOnFailure)
            {
                Console.Error.WriteLine(
                    "ERROR: --gateway-candidate-package requires --validate-gateway-candidate, headless mode, and rollback on failure.");
                return 2;
            }

            if (!InstallCliStep.TryValidateCandidatePackagePath(
                    gatewayCandidatePackage,
                    out var normalizedPackagePath,
                    out var packageError))
            {
                Console.Error.WriteLine($"ERROR: {packageError}");
                return 2;
            }

            config.Gateway.ValidationPackagePath = normalizedPackagePath;
        }

        if (!uninstall && !wizardOnly)
        {
            try
            {
                if (config.Gateway.ValidationPackagePath != null)
                    GatewayReleasePolicy.ResolveAndApplyValidationPackage(config);
                else
                    GatewayReleasePolicy.ResolveAndApply(
                        config,
                        allowCandidate: validateGatewayCandidate);
            }
            catch (GatewayCompatibilityException ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 2;
            }
        }

        if (TailscaleSetupPolicy.ValidateConfig(config) is { } tailscaleConfigError)
        {
            Console.Error.WriteLine($"ERROR: {tailscaleConfigError}");
            return 2;
        }

        // Default log path if not specified
        var logLabel = uninstall ? "uninstall" : "setup";
        config.LogPath ??= Path.Combine(
            dataDir ?? SetupContext.ResolveDataDir(),
            "Logs", "Setup", $"{logLabel}-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        Console.WriteLine($"Log file: {config.LogPath}");
        Console.WriteLine($"Distro: {config.DistroName}");
        Console.WriteLine($"Gateway: {config.EffectiveGatewayUrl}");
        if (!uninstall && !wizardOnly)
        {
            Console.WriteLine(
                $"Gateway release: {config.Gateway.Version} " +
                $"({config.Gateway.Selection}, protocol v{GatewayReleasePolicy.ProtocolGeneration})");
        }
        Console.WriteLine($"Mode: {(uninstall ? "UNINSTALL" : "SETUP")}");
        if (uninstall)
        {
            Console.WriteLine($"Dry run: {config.DryRun}");
            Console.WriteLine($"Confirm destructive: {config.ConfirmDestructive}");
        }
        Console.WriteLine();

        if (!uninstall &&
            !wizardOnly &&
            !DistroInstallPathPolicy.TryGetNewInstallPath(
                localDataDir ?? SetupContext.ResolveLocalDataDir(),
                config.DistroName,
                out _,
                out var distroPathError))
        {
            Console.Error.WriteLine(
                $"ERROR: {DistroInstallPathPolicy.WithLegacyReplacementGuidance(config.DistroName, distroPathError)}");
            return 2;
        }

        if (dryRun && !uninstall)
        {
            Console.WriteLine("DRY RUN: config validated, exiting.");
            return 0;
        }

        // Uninstall safety gate
        if (uninstall && !confirmDestructive && !dryRun)
        {
            Console.Error.WriteLine("ERROR: --uninstall requires --confirm-destructive (or use --dry-run to preview).");
            return 2;
        }

        if (!SetupRunLock.TryAcquire(dataDir ?? SetupContext.ResolveDataDir(), out var setupLock, out var lockMessage))
        {
            Console.Error.WriteLine($"ERROR: {lockMessage}");
            return 2;
        }

        using var acquiredSetupLock = setupLock;

        // Create infrastructure after acquiring the run lock so a concurrent loser
        // cannot truncate the active run's log or journal files.
        using var logger = new SetupLogger(config.LogPath, Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);
        var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
        using var journal = new TransactionJournal(journalPath, logger);
        var commands = new CommandRunner(logger);
        using var cts = new CancellationTokenSource();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.Warn("Cancellation requested (Ctrl+C)");
            cts.Cancel();
        };

        var ctx = new SetupContext(
            config,
            logger,
            journal,
            commands,
            cts.Token,
            dataDir,
            localDataDir,
            new ConsoleExternalAuthorizationPresenter());

        // Build step pipeline
        List<SetupStep> steps;
        if (uninstall)
        {
            steps = SetupStepFactory.BuildDefaultSteps();
        }
        else if (wizardOnly)
        {
            steps = SetupStepFactory.BuildWizardOnlySteps();
        }
        else
        {
            steps = BuildSteps(config);
        }

        var pipeline = new SetupPipeline(steps);

        pipeline.StepProgress += (_, e) =>
        {
            var icon = e.Outcome switch
            {
                StepOutcome.Success => "✓",
                StepOutcome.Skipped => "⊘",
                StepOutcome.Failed or StepOutcome.FailedTerminal => "✗",
                null => "►",
                _ => "?"
            };
            var elapsed = e.Elapsed.HasValue ? $" ({e.Elapsed.Value.TotalSeconds:F1}s)" : "";
            Console.WriteLine($"  {icon} {e.DisplayName}{elapsed}");
        };

        // Run!
        logger.Info(
            $"{(uninstall ? "Uninstall" : "Setup")} engine starting",
            new { version = AppVersionInfo.Version, args = string.Join(' ', args) });

        PipelineResult result;
        if (uninstall)
        {
            result = await pipeline.UninstallAsync(ctx);

            // Post-rollback tray-artifact cleanup (autostart, run.marker, settings, logs)
            if (SetupPipeline.ShouldRunTrayArtifactCleanup(result, config.DryRun))
            {
                logger.Info("Running tray-artifact cleanup...");
                TrayArtifactCleanup.Run(ctx, preserveLogs, autoStartName, startupTaskName);
            }
        }
        else
        {
            result = await pipeline.RunAsync(ctx);
        }

        Console.WriteLine();
        var label = uninstall ? "UNINSTALL" : "SETUP";
        Console.WriteLine(result.Outcome switch
        {
            PipelineOutcome.Success => $"═══ {label} COMPLETE ═══",
            PipelineOutcome.Failed => $"═══ {label} FAILED ═══\n  {result.Message}",
            PipelineOutcome.Cancelled => $"═══ {label} CANCELLED ═══",
            _ => "═══ UNKNOWN STATE ═══"
        });
        if (result.CompatibilityFailure is not null)
            Console.WriteLine($"\n{BuildCompatibilityFallbackMessage(config, result.CompatibilityFailure.Value)}");

        Console.WriteLine($"\nLog: {config.LogPath}");
        Console.WriteLine($"Journal: {journalPath}");

        // Write JSON output if requested (for programmatic callers like CliUninstallHandler)
        if (jsonOutput != null)
        {
            var outputDir = Path.GetDirectoryName(jsonOutput);
            if (outputDir != null) Directory.CreateDirectory(outputDir);

            var json = SerializeJsonOutput(result, config, journalPath);
            await AtomicFile.WriteAllTextAsync(jsonOutput, json);
        }

        return result.ExitCode;
    }

    internal static string SerializeJsonOutput(
        PipelineResult result,
        SetupConfig config,
        string journalPath)
    {
        var jsonResult = new
        {
            outcome = result.Outcome.ToString(),
            exitCode = result.ExitCode,
            failedStepId = result.FailedStepId,
            message = result.Message,
            requiresRestart = result.RequiresRestart,
            compatibilityFailure = result.CompatibilityFailure?.ToString(),
            selectedGatewayVersion = config.Gateway.Version,
            gatewayProtocolGeneration = GatewayReleasePolicy.ProtocolGeneration,
            fallbackGatewayVersion =
                result.CompatibilityFailure is { } failureKind &&
                GatewayReleasePolicy.CanRetryWithFallback(config, failureKind)
                    ? GatewayReleasePolicy.FallbackVersion
                    : null,
            logPath = config.LogPath,
            journalPath
        };
        return System.Text.Json.JsonSerializer.Serialize(jsonResult, SetupConfig.JsonWriteOptions);
    }

    internal static string BuildCompatibilityFallbackMessage(
        SetupConfig config,
        GatewayCompatibilityFailureKind failureKind)
    {
        var fallback = GatewayReleasePolicy.FallbackVersion;
        return !GatewayReleasePolicy.CanRetryWithFallback(config, failureKind)
            ? $"No additional validated fallback is available at or above security floor {GatewayReleasePolicy.SecurityFloor}."
            : $"To retry explicitly with validated fallback {fallback}, set Gateway.Selection to \"fallback\" and rerun setup.";
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps();

    internal static bool TryParseArguments(
        string[] args,
        out SetupArgumentParser.ParsedArguments parsedArguments,
        out string? error)
        => SetupArgumentParser.TryParse(
            args,
            ValueOptionNames,
            FlagOptionNames,
            out parsedArguments,
            out error);

    private static bool TryLoadConfig(string path, out SetupConfig config, out string? error)
    {
        if (SetupConfig.TryLoadFromFile(path, out var loadedConfig, out error))
        {
            config = loadedConfig;
            return true;
        }

        config = null!;
        return false;
    }
}

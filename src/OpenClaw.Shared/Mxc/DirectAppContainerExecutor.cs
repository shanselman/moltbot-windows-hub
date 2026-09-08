using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Implements <see cref="ISandboxExecutor"/> by spawning <c>wxc-exec.exe</c>
/// directly via <see cref="MxcExecutor"/>. No Node.js runtime required.
/// </summary>
/// <remarks>
/// Responsibilities:
/// <list type="bullet">
/// <item>Per-invocation scratch dir lifecycle.</item>
/// <item>Logging the final <see cref="MxcConfig"/> before sending — structured
///   summary by default; full JSON when <see cref="LogFullConfigEnvVar"/> is set.</item>
/// <item>Host-side timeout + cancel + process-tree kill via
///   <see cref="MxcExecutor"/>'s CancellationToken plumbing.</item>
/// <item>Cmd-line overflow handling — falls back to <c>--config &lt;file&gt;</c>
///   when the base64'd config exceeds the Windows command-line limit.</item>
/// </list>
/// </remarks>
public sealed class DirectAppContainerExecutor : ISandboxExecutor
{
    public string Name => "mxc-direct-appc";
    public bool IsContained => true;

    /// <summary>Default cap on stdout/stderr returned to the host (4 MiB).</summary>
    public const long DefaultMaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>
    /// When set to "1", the executor logs the FULL config JSON (paths, command
    /// line) instead of the redacted summary. Env values are still keys-only.
    /// Use for sandbox-debugging only; the redacted summary is the default.
    /// </summary>
    public const string LogFullConfigEnvVar = "OPENCLAW_MXC_LOG_FULL_CONFIG";

    /// <summary>
    /// Threshold for base64'd config length above which we switch to
    /// <c>--config &lt;file&gt;</c>. Windows cmdline is capped near 32k chars;
    /// 25k leaves headroom for the executable path and other args.
    /// </summary>
    private const int Base64ConfigCharLimit = 25_000;

    private readonly Func<MxcAvailability> _availabilityProvider;
    private readonly IOpenClawLogger _logger;

    /// <summary>
    /// Resolves availability lazily via <paramref name="availabilityProvider"/> so the
    /// executor always sees the current probe verdict. A fixed snapshot would freeze a
    /// startup probe error for the executor's lifetime even after the host recovers
    /// (the re-probe would update the caller's gate but not this executor).
    /// </summary>
    public DirectAppContainerExecutor(Func<MxcAvailability> availabilityProvider, IOpenClawLogger? logger = null)
    {
        _availabilityProvider = availabilityProvider ?? throw new ArgumentNullException(nameof(availabilityProvider));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default)
    {
        // Resolve the live availability for THIS invocation so a transient startup
        // probe error that has since recovered doesn't permanently fail us closed.
        var availability = _availabilityProvider();

        if (!availability.IsAppContainerAvailable)
        {
            throw new SandboxUnavailableException(
                availability.UnsupportedReasons.FirstOrDefault() ??
                "MXC process containment is unavailable.");
        }

        if (!availability.IsWxcExecResolvable || string.IsNullOrEmpty(availability.WxcExecPath))
            throw new SandboxUnavailableException("wxc-exec.exe not found");

        if (!availability.CanRunSystemRunSandbox)
        {
            throw new SandboxUnavailableException(
                availability.SystemRunSandboxUnsupportedReasons.FirstOrDefault() ??
                "OpenClaw Node Sandbox requires MXC BaseContainer without host DACL augmentation.");
        }

        var capBytes = request.MaxOutputBytes is > 0 ? request.MaxOutputBytes.Value : DefaultMaxOutputBytes;
        var capInt = capBytes > int.MaxValue ? int.MaxValue : (int)capBytes;

        var scratchDir = CreateScratchDir();
        string? tempConfigFile = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var config = MxcConfigBuilder.Build(request, scratchDir);
            if (!MxcIsolationTierPolicy.IsSystemRunConfigBaseContainerCompatible(config))
            {
                throw new SandboxUnavailableException(
                    "OpenClaw's emitted system.run policy is not compatible with the admitted MXC BaseContainer contract.");
            }
            config = MxcConfigBuilder.WithNonCascadingVolumeRootGrants(config);
            var configJson = JsonSerializer.Serialize(config, ConfigJson);
            var launchWorkingDirectory = string.IsNullOrWhiteSpace(config.Process.Cwd)
                ? null
                : config.Process.Cwd;

            WarnIfUnsupportedVolume(config);
            LogConfig(config, configJson, request, availability);

            MxcExecutor executor;
            try
            {
                executor = new MxcExecutor(availability.WxcExecPath, stdoutCapBytes: capInt, stderrCapBytes: capInt);
            }
            catch (FileNotFoundException ex)
            {
                throw new SandboxUnavailableException($"wxc-exec.exe not found at {availability.WxcExecPath}", ex);
            }

            // Local timeout + caller cancellation. Mirror the builder's
            // effective timeout (request.TimeoutMs > 0 ? request.TimeoutMs :
            // DefaultProcessTimeoutMs) so a request with TimeoutMs=0 doesn't
            // skip the host-side bound entirely. Add a grace window above
            // the per-process timeout so wxc-exec has a chance to clean up
            // before we kill its process tree.
            var effectiveTimeoutMs = request.TimeoutMs > 0
                ? request.TimeoutMs
                : MxcConfigBuilder.DefaultProcessTimeoutMs;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(effectiveTimeoutMs + 5_000);

            MxcResult result;
            // Base64 cmdline grows ~4/3 vs the underlying JSON bytes. Count
            // UTF-8 bytes (not chars) because non-ASCII text would otherwise
            // under-estimate the encoded size and overflow the cmdline limit.
            var configByteCount = Encoding.UTF8.GetByteCount(configJson);
            var base64Len = ((configByteCount + 2) / 3) * 4;
            if (base64Len <= Base64ConfigCharLimit)
            {
                result = await executor.RunAsync(config, linked.Token, workingDirectory: launchWorkingDirectory);
            }
            else
            {
                tempConfigFile = Path.Combine(scratchDir, "wxc-config.json");
                await File.WriteAllTextAsync(tempConfigFile, configJson, Encoding.UTF8, linked.Token);
                result = await executor.RunWithConfigFileAsync(tempConfigFile, linked.Token, workingDirectory: launchWorkingDirectory);
            }

            sw.Stop();

            // Caller-cancellation precedence: if the caller's token tripped,
            // surface as OperationCanceledException instead of falsely
            // reporting TimedOut. Check this BEFORE the timeout branch so a
            // race between caller-cancel and linked-cancel resolves correctly.
            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            // If our linked token tripped (timeout) and the caller's didn't, surface
            // as a TimedOut result rather than throwing.
            var timedOut = result.TimedOut || linked.IsCancellationRequested;
            if (timedOut)
            {
                return new SandboxExecutionResult(
                    ExitCode: -1,
                    Stdout: result.Output ?? string.Empty,
                    Stderr: result.Error ?? "Sandboxed invocation timed out.",
                    TimedOut: true,
                    DurationMs: result.DurationMs == 0 ? sw.ElapsedMilliseconds : result.DurationMs,
                    ContainmentTag: "mxc",
                    StructuredResult: null);
            }

            return new SandboxExecutionResult(
                ExitCode: result.ExitCode,
                Stdout: result.Output ?? string.Empty,
                Stderr: result.Error ?? string.Empty,
                TimedOut: false,
                DurationMs: result.DurationMs == 0 ? sw.ElapsedMilliseconds : result.DurationMs,
                ContainmentTag: "mxc",
                StructuredResult: null);
        }
        finally
        {
            TryDelete(tempConfigFile);
            TryDeleteDir(scratchDir);
        }
    }

    private static string CreateScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-mxc-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Trace.WriteLine($"DirectAppContainerExecutor.TryDelete '{path}' (best-effort) failed: {ex.Message}"); }
    }

    private static void TryDeleteDir(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Trace.WriteLine($"DirectAppContainerExecutor.TryDeleteDir '{path}' (best-effort) failed: {ex.Message}"); }
    }

    private void LogConfig(MxcConfig config, string configJson, SandboxExecutionRequest request, MxcAvailability availability)
    {
        // Default: redacted summary. Field counts only; no paths, no command line,
        // no env values. Useful for verifying Sandbox UI settings round-tripped
        // into wxc-exec without leaking the user's filesystem layout.
        var envKeys = config.Process.Env?
            .Select(kv => kv.Split('=', 2)[0])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var summary = BuildRedactedConfigSummary(availability, config, configJson, request);
        _logger.Debug(summary);
        Trace.WriteLine(summary);

        // Full repro: gated behind env var. Paths and command line included;
        // env values still redacted (keys only) to avoid leaking caller tokens.
        if (string.Equals(Environment.GetEnvironmentVariable(LogFullConfigEnvVar), "1", StringComparison.Ordinal))
        {
            var redactedConfig = config with
            {
                Process = config.Process with
                {
                    Env = envKeys.Select(k => k + "=<redacted>").ToArray(),
                }
            };
            var fullJson = JsonSerializer.Serialize(redactedConfig, ConfigJson);
            var fullMsg = $"[mxc] wxc-exec config (full, env-values redacted) configJson={fullJson}";
            _logger.Debug(fullMsg);
            Trace.WriteLine(fullMsg);
        }
    }

    internal static string BuildRedactedConfigSummary(
        MxcAvailability availability,
        MxcConfig config,
        string configJson,
        SandboxExecutionRequest request)
    {
        // Default diagnostics must not expose host paths. Keep path presence as
        // a state flag; the opt-in full diagnostics channel owns path-bearing repro data.
        var wxcExecState = availability.IsWxcExecResolvable && !string.IsNullOrWhiteSpace(availability.WxcExecPath)
            ? "<set>"
            : "<missing>";
        var envKeys = config.Process.Env?
            .Select(kv => kv.Split('=', 2)[0])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return
            "[mxc] wxc-exec config (redacted) " +
            $"wxcExec={wxcExecState}; configBytes={Encoding.UTF8.GetByteCount(configJson)}; " +
            $"containerId={config.ContainerId}; version={config.Version}; " +
            $"commandLineLength={config.Process.CommandLine?.Length ?? 0}; " +
            $"cwd={(string.IsNullOrEmpty(config.Process.Cwd) ? "<null>" : "<set>")}; " +
            $"envKeys=[{string.Join(",", envKeys)}]; " +
            $"timeoutMs={config.Process.TimeoutMs?.ToString() ?? "<null>"}; " +
            $"capabilities=[{string.Join(",", config.ProcessContainer?.Capabilities ?? Array.Empty<string>())}]; " +
            $"readonlyCount={config.Filesystem?.ReadonlyPaths?.Length ?? 0}; " +
            $"readwriteCount={config.Filesystem?.ReadwritePaths?.Length ?? 0}; " +
            $"deniedCount={config.Filesystem?.DeniedPaths?.Length ?? 0}; " +
            $"network={{defaultPolicy={config.Network?.DefaultPolicy ?? "<null>"},enforcementMode={config.Network?.EnforcementMode ?? "<null>"}}}; " +
            $"ui={{disable={config.Ui?.Disable},clipboard={config.Ui?.Clipboard ?? "<null>"},injection={config.Ui?.Injection}}}; " +
            $"maxOutputBytes={request.MaxOutputBytes?.ToString() ?? "<default>"}";
    }

    private void WarnIfUnsupportedVolume(MxcConfig config)
    {
        var paths = (config.Filesystem?.ReadonlyPaths ?? Array.Empty<string>())
            .Concat(config.Filesystem?.ReadwritePaths ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var warnedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(root) || !warnedRoots.Add(root))
                continue;

            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    continue;

                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn(
                        $"[mxc] sandbox filesystem grants may be unsupported for volume {root}; " +
                        "commands that need that path may fail.");
                }
            }
            catch (Exception ex)
            {
                // Best-effort diagnostic only. The command result should reflect
                // the real MXC failure if the volume cannot be queried.
                _logger.Debug($"DirectAppContainerExecutor: volume filesystem probe failed: {ex.Message}");
            }
        }
    }

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

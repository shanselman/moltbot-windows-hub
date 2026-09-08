using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Per-backend availability probe for MXC. <see cref="Probe"/> is intended to be
/// called once and its result cached by the caller (see NodeService).
/// </summary>
/// <remarks>
/// Backends checked:
/// <list type="bullet">
/// <item><see cref="IsAppContainerAvailable"/> — the native <c>wxc-exec --probe</c>
///   reports a usable isolation tier on this host. This is the source of truth
///   for "does this machine support the sandbox".</item>
/// <item><see cref="IsWxcExecResolvable"/> — wxc-exec.exe found in the shipped tray output layout or via override.</item>
/// <item><see cref="IsIsolationSessionAvailable"/> — requires a supported host plus IsolationProxy.exe in System32.</item>
/// </list>
/// </remarks>
public sealed class MxcAvailability
{
    /// <summary>
    /// Optional override path for <c>wxc-exec.exe</c>. When set, used instead of
    /// probing the shipped <c>tools\mxc\&lt;arch&gt;\wxc-exec.exe</c> layout. Wired
    /// through environment variable <c>OPENCLAW_WXC_EXEC</c>.
    /// </summary>
    public const string WxcExecOverrideEnvVar = "OPENCLAW_WXC_EXEC";

    /// <summary>
    /// Bound on how long we wait for <c>wxc-exec --probe</c> before treating the
    /// host as unable to run the sandbox.
    /// </summary>
    private const int ProbeTimeoutMs = 15_000;

    /// <summary>
    /// Minimum time allowed for draining stdout/stderr after the probe process
    /// exits, so a process that exits right at the deadline doesn't false-timeout
    /// while its (tiny) output is still being read.
    /// </summary>
    private const int MinReadDrainMs = 2_000;

    public bool IsAppContainerAvailable { get; }
    public bool IsIsolationSessionAvailable { get; }
    public bool IsWxcExecResolvable { get; }
    public string? WxcExecPath { get; }
    public bool ProbeSuppressedBySkuGate { get; }

    /// <summary>
    /// True when the availability verdict came from a <em>probe error</em>
    /// (timeout, failure to launch <c>wxc-exec</c>, or unparseable output) rather
    /// than a definitive answer (supported, or the host reporting unsupported).
    /// Callers should treat an errored verdict as transient — re-probe instead of
    /// caching it for the process lifetime — so a momentary glitch doesn't pin the
    /// whole process to uncontained execution.
    /// </summary>
    public bool ProbeErrored { get; }

    /// <summary>
    /// Isolation tier the probe selected for this host (e.g. <c>base-container</c>,
    /// <c>appcontainer-bfs</c>, <c>appcontainer-dacl</c>), or null when unsupported
    /// / not probed. <see cref="CanRunSystemRunSandbox"/> is the narrower
    /// OpenClaw process-containment admission signal.
    /// </summary>
    public string? IsolationTier { get; }

    /// <summary>
    /// True when the probe indicated the host needs host-DACL augmentation to
    /// contain (a weaker, last-resort path).
    /// </summary>
    public bool NeedsDaclAugmentation { get; }

    /// <summary>
    /// Operator-visible warnings emitted by <c>wxc-exec --probe</c>.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// True when this host can run OpenClaw <c>system.run</c> with MXC's
    /// BaseContainer tier and no host-DACL augmentation.
    /// </summary>
    public bool CanRunSystemRunSandbox =>
        IsAppContainerAvailable &&
        IsWxcExecResolvable &&
        string.Equals(
            IsolationTier,
            MxcIsolationTierPolicy.BaseContainer,
            StringComparison.Ordinal) &&
        !NeedsDaclAugmentation;

    /// <summary>
    /// Human-readable list of reasons MXC may not be available. Empty when fully supported.
    /// Surface to UX so users know why the sandbox toggle is disabled.
    /// </summary>
    public IReadOnlyList<string> UnsupportedReasons { get; }

    /// <summary>
    /// Reasons OpenClaw cannot use this probe result for <c>system.run</c>.
    /// General MXC or session-containment availability remains separate.
    /// </summary>
    public IReadOnlyList<string> SystemRunSandboxUnsupportedReasons
    {
        get
        {
            if (CanRunSystemRunSandbox)
                return Array.Empty<string>();

            if (UnsupportedReasons.Count > 0)
                return UnsupportedReasons;

            var tier = string.IsNullOrWhiteSpace(IsolationTier)
                ? "no process-containment tier"
                : IsolationTier;
            var dacl = NeedsDaclAugmentation
                ? " Host DACL augmentation is required."
                : string.Empty;
            return
            [
                $"OpenClaw Node Sandbox requires MXC BaseContainer without host DACL augmentation. " +
                $"This PC reports {tier}.{dacl}",
            ];
        }
    }

    /// <summary>True iff at least one MXC backend is supported AND
    /// <c>wxc-exec.exe</c> is resolvable. (Without wxc-exec the executor will refuse
    /// to run, so reporting "available" would lie to the UI.)</summary>
    public bool HasAnyBackend =>
        (IsAppContainerAvailable || IsIsolationSessionAvailable)
        && IsWxcExecResolvable;

    public MxcAvailability(
        bool isAppContainerAvailable,
        bool isIsolationSessionAvailable,
        bool isWxcExecResolvable,
        string? wxcExecPath,
        IReadOnlyList<string> unsupportedReasons,
        bool probeErrored = false,
        string? isolationTier = null,
        bool needsDaclAugmentation = false,
        IReadOnlyList<string>? warnings = null,
        bool probeSuppressedBySkuGate = false)
    {
        IsAppContainerAvailable = isAppContainerAvailable;
        IsIsolationSessionAvailable = isIsolationSessionAvailable;
        IsWxcExecResolvable = isWxcExecResolvable;
        WxcExecPath = wxcExecPath;
        UnsupportedReasons = unsupportedReasons;
        ProbeErrored = probeErrored;
        IsolationTier = isolationTier;
        NeedsDaclAugmentation = needsDaclAugmentation;
        Warnings = warnings ?? Array.Empty<string>();
        ProbeSuppressedBySkuGate = probeSuppressedBySkuGate;
    }

    /// <summary>
    /// Probe the running environment. Designed to be called once at app startup
    /// and the result cached. Host support is determined by the native
    /// <c>wxc-exec --probe</c> command rather than a hardcoded build/UBR table,
    /// so newer Windows builds light up automatically without a code change.
    /// </summary>
    public static MxcAvailability Probe(IOpenClawLogger? logger = null)
        => Probe(logger, probeRunner: null);

    /// <summary>
    /// Test seam for <see cref="Probe(IOpenClawLogger?)"/>. <paramref name="probeRunner"/>
    /// substitutes the <c>wxc-exec --probe</c> invocation; production passes
    /// <c>null</c> to spawn the real process.
    /// </summary>
    internal static MxcAvailability Probe(
        IOpenClawLogger? logger,
        Func<string, WxcProbeInvocation>? probeRunner,
        Func<bool?>? windowsServerProvider = null,
        Func<bool>? windowsProvider = null,
        Func<(bool Resolvable, string? Path)>? wxcResolver = null)
    {
        var log = logger ?? NullLogger.Instance;
        var reasons = new List<string>();

        if (!(windowsProvider?.Invoke() ?? OperatingSystem.IsWindows()))
        {
            reasons.Add("MXC requires Windows.");
            return new MxcAvailability(false, false, false, null, reasons);
        }

        var isWindowsServer = (windowsServerProvider ?? DetectWindowsServerSku)();
        if (isWindowsServer != false)
        {
            var reason = isWindowsServer == true
                ? "Windows Server does not support OpenClaw MXC containment."
                : "OpenClaw could not verify that this is a supported Windows client SKU.";
            reasons.Add(reason);
            log.Warn(
                $"[mxc] availability: supported=false sku=" +
                $"{(isWindowsServer == true ? "windows_server" : "unknown")} probe=skipped");
            return new MxcAvailability(
                false,
                false,
                false,
                null,
                reasons,
                probeErrored: isWindowsServer is null,
                probeSuppressedBySkuGate: true);
        }

        // wxc-exec is the source of truth for host support, so resolve it first.
        // Without the binary we cannot probe and therefore report unavailable.
        var (wxcResolvable, wxcPath) = (wxcResolver ?? ResolveWxcExec)();
        if (!wxcResolvable || string.IsNullOrEmpty(wxcPath))
        {
            reasons.Add($"wxc-exec.exe not found. Set {WxcExecOverrideEnvVar} or build the tray app to copy it into the output folder.");
            return new MxcAvailability(false, false, false, null, reasons);
        }

        // Ask the native binary whether this host can run the sandbox and at what
        // isolation tier.
        WxcProbeInvocation invocation;
        try
        {
            invocation = (probeRunner ?? RunWxcExecProbe)(wxcPath);
        }
        catch (Exception ex)
        {
            invocation = new WxcProbeInvocation(WxcProbeStatus.LaunchFailed, 0, string.Empty, $"Failed to launch wxc-exec --probe: {ex.Message}");
        }

        var probe = ParseProbeOutput(invocation.Status, invocation.ExitCode, invocation.StdOut, invocation.StdErr);
        var isAppContainerSupported = probe.Supported;
        if (!isAppContainerSupported)
            reasons.Add(probe.FailureReason ?? "This Windows host does not support the MXC sandbox (wxc-exec --probe reported no usable tier).");

        // isolation_session additionally requires Feature_IsoBrokerSessionApis on the OS
        // and IsolationProxy.exe in System32. We currently only check file presence.
        var isolationProxyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "IsolationProxy.exe");
        var isIsolationSessionSupported = isAppContainerSupported && File.Exists(isolationProxyPath);

        var probeErrored = probe.Outcome == MxcProbeOutcome.ProbeError;

        log.Info(
            $"[mxc] availability: supported={isAppContainerSupported} " +
            $"outcome={probe.Outcome} " +
            $"tier={probe.Tier ?? "<none>"} needsDaclAugmentation={probe.NeedsDaclAugmentation} " +
            $"isolation_session={isIsolationSessionSupported} " +
            $"wxc-exec={wxcPath} " +
            $"warnings=[{string.Join(", ", probe.Warnings)}] " +
            $"reasons=[{string.Join(", ", reasons)}]");

        return new MxcAvailability(
            isAppContainerSupported,
            isIsolationSessionSupported,
            isWxcExecResolvable: true,
            wxcPath,
            reasons,
            probeErrored: probeErrored,
            isolationTier: probe.Tier,
            needsDaclAugmentation: probe.NeedsDaclAugmentation,
            warnings: probe.Warnings);
    }

    internal static bool? DetectWindowsServerSku()
    {
        try
        {
            // Managed equivalent of VersionHelpers.h IsWindowsServer(). A false
            // workstation comparison with ERROR_OLD_WIN_VERSION means Server.
            var versionInfo = new OsVersionInfoEx
            {
                OsVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfoEx>(),
                ServicePack = string.Empty,
                ProductType = NativeMethods.VerNtWorkstation,
            };
            var conditionMask = NativeMethods.VerSetConditionMask(
                0,
                NativeMethods.VerProductType,
                NativeMethods.VerEqual);

            if (NativeMethods.VerifyVersionInfo(
                    ref versionInfo,
                    NativeMethods.VerProductType,
                    conditionMask))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() == NativeMethods.ErrorOldWinVersion
                ? true
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or MarshalDirectiveException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public uint OsVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? ServicePack;

        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    private static class NativeMethods
    {
        internal const uint VerProductType = 0x00000080;
        internal const byte VerEqual = 1;
        internal const byte VerNtWorkstation = 1;
        internal const int ErrorOldWinVersion = 1150;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VerifyVersionInfo(
            ref OsVersionInfoEx versionInfo,
            uint typeMask,
            ulong conditionMask);

        [DllImport("kernel32.dll")]
        internal static extern ulong VerSetConditionMask(
            ulong conditionMask,
            uint typeMask,
            byte condition);
    }

    /// <summary>
    /// Parse the result of a <c>wxc-exec --probe</c> attempt into a support verdict,
    /// classifying the outcome as <see cref="MxcProbeOutcome.Supported"/>,
    /// <see cref="MxcProbeOutcome.UnsupportedHost"/> (the binary ran to completion and
    /// reported no usable tier), or <see cref="MxcProbeOutcome.ProbeError"/> (we could
    /// not get a verdict: timeout, launch failure, or a completed run with no usable
    /// output). Probe errors are transient and should be re-probed, not cached.
    /// Exposed for unit testing.
    /// </summary>
    internal static MxcProbeResult ParseProbeOutput(WxcProbeStatus status, int exitCode, string? stdout, string? stderr)
    {
        // We never obtained a verdict — our own infrastructure failure, not a host
        // decision. Always transient/retryable, regardless of any exit code.
        if (status != WxcProbeStatus.Completed)
        {
            var detail = FirstNonEmpty(stderr, stdout);
            var what = status == WxcProbeStatus.TimedOut ? "timed out" : "could not be launched";
            return Error(
                $"Could not determine MXC sandbox support (wxc-exec --probe {what}{(detail is null ? "" : $": {Summarize(detail)}")}).");
        }

        // No/garbled output is a binary anomaly, not a clear answer —
        // treat as a (retryable) probe error rather than silently "unsupported".
        if (string.IsNullOrWhiteSpace(stdout))
        {
            var detail = FirstNonEmpty(stderr);
            return Error(
                $"Could not determine MXC sandbox support (wxc-exec --probe {(exitCode == 0 ? "returned no output" : $"exited {exitCode}")}{(detail is null ? "" : $": {Summarize(detail)}")}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(
                    "Could not determine MXC sandbox support (wxc-exec --probe returned a non-object JSON value).");
            }

            var warnings = ReadWarnings(root);

            // Explicit unsupported verdicts are already fail-closed and do not
            // need tier metadata. Validate needsDaclAugmentation only before
            // accepting a candidate supported result; otherwise an unsupported
            // host would be re-probed indefinitely as a transient parse error.
            if (TryGetString(root, "error") is { } error)
            {
                return Unsupported(
                    $"This Windows host does not support the MXC sandbox (wxc-exec --probe reported: {Summarize(error)}).",
                    warnings);
            }

            if (root.TryGetProperty("supported", out var supportedEl)
                && supportedEl.ValueKind == JsonValueKind.False)
            {
                return Unsupported(
                    "This Windows host does not support the MXC sandbox (wxc-exec --probe reported no usable isolation tier).",
                    warnings);
            }

            if (exitCode != 0)
            {
                var detail = FirstNonEmpty(stderr, stdout);
                return Error(
                    $"Could not determine MXC sandbox support (wxc-exec --probe exited {exitCode}{(detail is null ? "" : $": {Summarize(detail)}")}).");
            }

            if (!root.TryGetProperty("tier", out var tierEl)
                || tierEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tierEl.GetString()))
            {
                return Error("Could not determine MXC sandbox support (wxc-exec --probe did not report an isolation tier).");
            }

            if (!root.TryGetProperty("needsDaclAugmentation", out var d) ||
                d.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Error(
                    "Could not determine MXC sandbox support (wxc-exec --probe did not report a boolean needsDaclAugmentation value).");
            }

            var needsDacl = d.ValueKind == JsonValueKind.True;

            return new MxcProbeResult(MxcProbeOutcome.Supported, tierEl.GetString(), needsDacl, warnings, null);
        }
        catch (JsonException ex)
        {
            var detail = FirstNonEmpty(stderr, stdout);
            return Error(
                $"Could not determine MXC sandbox support (wxc-exec --probe {(exitCode == 0 ? "returned unparseable output" : $"exited {exitCode}")}: {Summarize(detail ?? ex.Message)}).");
        }

        static MxcProbeResult Unsupported(string reason, IReadOnlyList<string>? warnings = null) =>
            new(MxcProbeOutcome.UnsupportedHost, null, false, warnings ?? Array.Empty<string>(), reason);

        static MxcProbeResult Error(string reason) =>
            new(MxcProbeOutcome.ProbeError, null, false, Array.Empty<string>(), reason);

        static string? TryGetString(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        static List<string> ReadWarnings(JsonElement root)
        {
            var warnings = new List<string>();
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("warnings", out var w)
                || w.ValueKind != JsonValueKind.Array)
            {
                return warnings;
            }

            foreach (var item in w.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) warnings.Add(s);
            }

            return warnings;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        return null;
    }

    private static string Summarize(string text)
    {
        var collapsed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "…";
    }

    /// <summary>
    /// Spawn <c>wxc-exec --probe</c> and capture its exit code and output.
    /// Bounded by <see cref="ProbeTimeoutMs"/>; a timeout reports a non-zero exit.
    /// </summary>
    private static WxcProbeInvocation RunWxcExecProbe(string wxcExecPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = wxcExecPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.ArgumentList.Add("--probe");

        process.Start();
        // Read both pipes async to avoid a full-pipe deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var sw = Stopwatch.StartNew();
        if (!process.WaitForExit(ProbeTimeoutMs))
            return KillAndTimeout(process, stdoutTask, stderrTask);

        // WaitForExit(int) returns as soon as the immediate child exits, but the
        // async readers only complete once the WRITE end of each pipe closes. If
        // wxc-exec spawned a descendant that inherited the redirected handles
        // (the SDK ships sandbox daemon/guest helpers), the pipes can stay open
        // after the child exits and an unbounded GetResult() would hang past the
        // timeout. So bound the drain with whatever time is left in the budget
        // (with a small floor so a near-deadline exit doesn't false-timeout).
        var elapsedMs = (int)Math.Min(sw.ElapsedMilliseconds, ProbeTimeoutMs);
        var drainBudgetMs = Math.Max(ProbeTimeoutMs - elapsedMs, MinReadDrainMs);
        try
        {
            if (!Task.WhenAll(stdoutTask, stderrTask).Wait(drainBudgetMs))
                return KillAndTimeout(process, stdoutTask, stderrTask);
        }
        catch (AggregateException)
        {
            // A pipe read faulted (e.g. stream disposed). Treat as no output;
            // ParseProbeOutput classifies exit 0 with empty stdout as a (retryable)
            // probe error, and surfaces stderr if the exit code was non-zero.
        }

        return new WxcProbeInvocation(
            WxcProbeStatus.Completed,
            process.ExitCode,
            stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : string.Empty,
            stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : string.Empty);
    }

    /// <summary>
    /// Kill the probe process tree and return a timeout invocation. Observes the
    /// abandoned read tasks so a later fault doesn't surface as an unobserved
    /// task exception when the process/streams are disposed.
    /// </summary>
    private static WxcProbeInvocation KillAndTimeout(Process process, Task stdoutTask, Task stderrTask)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        ObserveQuietly(stdoutTask);
        ObserveQuietly(stderrTask);
        return new WxcProbeInvocation(WxcProbeStatus.TimedOut, 0, string.Empty, "wxc-exec --probe timed out.");
    }

    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static (bool resolvable, string? path) ResolveWxcExec()
    {
        var overridePath = Environment.GetEnvironmentVariable(WxcExecOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return (true, overridePath);

        var arch = GetSdkArchString();
        var probeRoots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(MxcAvailability).Assembly.Location) ?? string.Empty,
        };

        foreach (var root in probeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            // Preferred: tools/mxc/<arch>/wxc-exec.exe — the layout the build
            // target extracts to so we don't ship a node_modules/ tree.
            var shipped = Path.Combine(root, "tools", "mxc", arch, "wxc-exec.exe");
            if (File.Exists(shipped))
                return (true, shipped);

            // Legacy fallback: developer builds with node_modules/ still around.
            var legacy = Path.Combine(
                root,
                "node_modules", "@microsoft", "mxc-sdk", "bin", arch, "wxc-exec.exe");
            if (File.Exists(legacy))
                return (true, legacy);
        }

        return (false, null);
    }

    /// <summary>Returns "arm64" or "x64" matching the <c>@microsoft/mxc-sdk</c> <c>bin/&lt;arch&gt;/</c> layout.</summary>
    private static string GetSdkArchString() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        _ => "x64",
    };
}

/// <summary>Outcome of attempting to run <c>wxc-exec --probe</c> (distinct from the
/// host verdict it produces). Lets us tell our own infrastructure failures
/// (timeout / launch failure) apart from a process that actually completed.</summary>
internal enum WxcProbeStatus
{
    /// <summary>The process ran to completion; <c>ExitCode</c> and output are meaningful.</summary>
    Completed,

    /// <summary>The process did not exit within the timeout and was killed.</summary>
    TimedOut,

    /// <summary>The process could not be started at all.</summary>
    LaunchFailed,
}

/// <summary>Raw result of invoking <c>wxc-exec --probe</c>. <c>ExitCode</c> is only
/// meaningful when <c>Status</c> is <see cref="WxcProbeStatus.Completed"/>.</summary>
internal readonly record struct WxcProbeInvocation(WxcProbeStatus Status, int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Classification of a <c>wxc-exec --probe</c> attempt.
/// </summary>
internal enum MxcProbeOutcome
{
    /// <summary>The probe ran and reported a usable isolation tier.</summary>
    Supported,

    /// <summary>The probe ran and definitively reported the host cannot sandbox.</summary>
    UnsupportedHost,

    /// <summary>
    /// We could not obtain a verdict (timeout, failure to launch, or exit 0 with
    /// no usable output). Transient/indeterminate — should be re-probed, not cached.
    /// </summary>
    ProbeError,
}

/// <summary>Parsed host-support verdict derived from <c>wxc-exec --probe</c> output.</summary>
internal sealed record MxcProbeResult(
    MxcProbeOutcome Outcome,
    string? Tier,
    bool NeedsDaclAugmentation,
    IReadOnlyList<string> Warnings,
    string? FailureReason)
{
    /// <summary>Convenience: true only for <see cref="MxcProbeOutcome.Supported"/>.</summary>
    public bool Supported => Outcome == MxcProbeOutcome.Supported;
}

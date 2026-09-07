using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.E2ETests;

/// <summary>
/// E2E tests are intentionally opt-in for local development because they
/// provision WSL, launch the tray, and dominate local test runtime. CI sets
/// OPENCLAW_RUN_E2E=1 so these still run before merge.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!E2ETestGate.IsEnabled)
            Skip = $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
    }
}

/// <summary>
/// Ollama Gateway E2E proof is opt-in because hosted runners do not provide a
/// local model service. The selected model is explicit so a proof cannot
/// silently download or substitute a different model.
/// </summary>
public sealed class OllamaGatewayE2EFactAttribute : FactAttribute
{
    public const string EnableEnvVar = "OPENCLAW_RUN_OLLAMA_E2E";

    public OllamaGatewayE2EFactAttribute()
    {
        if (!E2ETestGate.IsEnabled)
        {
            Skip = $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
            return;
        }

        if (!IsEnabled("GITHUB_ACTIONS") && !IsEnabled(EnableEnvVar))
            Skip = $"Ollama E2E proof disabled. Set {EnableEnvVar}=1 to enable.";
    }

    private static bool IsEnabled(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Focused E2E tests that require MXC support must not fail the regular E2E
/// shard on Windows runners where the Gateway path works but MXC is unavailable.
/// </summary>
public sealed class MxcE2EFactAttribute : FactAttribute
{
    public MxcE2EFactAttribute()
    {
        Skip = MxcE2ETestGate.SkipReason;
    }
}

internal static class MxcE2ETestGate
{
    private const string GitHubActionsEnvVar = "GITHUB_ACTIONS";
    private const string ExplicitGitHubActionsOptInEnvVar = "OPENCLAW_RUN_MXC_E2E";
    private static readonly Lazy<string?> s_skipReason = new(GetSkipReason);

    public static string? SkipReason => s_skipReason.Value;

    private static string? GetSkipReason()
    {
        if (!E2ETestGate.IsEnabled)
            return $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";

        if (IsEnabled(GitHubActionsEnvVar) && !IsEnabled(ExplicitGitHubActionsOptInEnvVar))
        {
            return "MXC E2E test skipped in GitHub Actions because hosted runners do not provide a working MXC/AppContainer runtime. " +
                $"Run on a local MXC-enabled Windows machine, or set {ExplicitGitHubActionsOptInEnvVar}=1 only on an MXC-enabled self-hosted runner.";
        }

        try
        {
            var availability = ProbeAvailabilityForE2E();
            if (!availability.CanRunSystemRunSandbox)
            {
                var reason = availability.SystemRunSandboxUnsupportedReasons.Count == 0
                    ? "MXC BaseContainer is unavailable."
                    : string.Join("; ", availability.SystemRunSandboxUnsupportedReasons);
                return $"MXC E2E test skipped: {reason}";
            }

            if (!availability.IsWxcExecResolvable && !TryFindE2EWxcExec(out _))
            {
                var reason = availability.UnsupportedReasons.Count == 0
                    ? "wxc-exec.exe is unavailable."
                    : string.Join("; ", availability.UnsupportedReasons);
                return $"MXC E2E test skipped: {reason}";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"MXC E2E test skipped: availability probe failed ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    private static MxcAvailability ProbeAvailabilityForE2E()
    {
        if (TryFindE2EWxcExec(out var wxcExecPath))
        {
            var previousOverride = Environment.GetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, wxcExecPath);
                return MxcAvailability.Probe();
            }
            finally
            {
                Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, previousOverride);
            }
        }

        return MxcAvailability.Probe();
    }

    private static bool TryFindE2EWxcExec(out string? path)
    {
        var overridePath = Environment.GetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar);
        if (FileExists(overridePath))
        {
            path = overridePath;
            return true;
        }

        foreach (var repoRoot in CandidateRepoRoots())
        {
            var arch = GetSdkArchString();
            var nodeModulesWxcExec = Path.Combine(repoRoot, "node_modules", "@microsoft", "mxc-sdk", "bin", arch, "wxc-exec.exe");
            if (FileExists(nodeModulesWxcExec))
            {
                path = nodeModulesWxcExec;
                return true;
            }

            var trayBin = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "bin");
            if (Directory.Exists(trayBin))
            {
                try
                {
                    var trayWxcExec = Directory.EnumerateFiles(trayBin, "wxc-exec.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(file => file.EndsWith(Path.Combine("mxc", arch, "wxc-exec.exe"), StringComparison.OrdinalIgnoreCase));
                    if (FileExists(trayWxcExec))
                    {
                        path = trayWxcExec;
                        return true;
                    }
                }
                catch
                {
                    // Discovery-only guard; a failed search should become an
                    // ordinary MXC skip rather than a discovery failure.
                }
            }
        }

        path = null;
        return false;
    }

    private static IEnumerable<string> CandidateRepoRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[]
        {
            Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            var dir = Directory.Exists(start)
                ? new DirectoryInfo(start)
                : new FileInfo(start).Directory;
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(dir.FullName, "src", "OpenClaw.Tray.WinUI")))
                {
                    if (seen.Add(dir.FullName))
                        yield return dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }
        }
    }

    private static bool FileExists(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    private static bool IsEnabled(string envVar) =>
        Environment.GetEnvironmentVariable(envVar) is { } value &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    private static string GetSdkArchString() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        _ => "x64",
    };
}

internal static class E2ETestGate
{
    public const string EnvVar = "OPENCLAW_RUN_E2E";

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnvVar) is { } value &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}

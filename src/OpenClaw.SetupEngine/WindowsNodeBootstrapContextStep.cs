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

internal sealed record WindowsNodeContextTarget(string DistroName, string User, string WorkspacePath);

internal sealed class WindowsNodeContextInstallState
{
    public List<WindowsNodeContextTarget> Targets { get; set; } = [];
}

public sealed class WindowsNodeBootstrapContextStep : SetupStep
{
    private const string InstallStateFileName = "windows-node-context.json";
    private WindowsNodeContextTarget? _currentTarget;
    private bool _currentTargetWasNew;
    private bool _executeAttempted;

    public override string Id => "windows-node-context";
    public override string DisplayName => "Inject Windows node context";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.WindowsNodeContext.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        _executeAttempted = true;
        var distro = ctx.DistroName!;
        var user = ctx.Config.Wsl.User;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, ctx.Config.WindowsNodeContext.TimeoutSeconds));

        var home = await ResolveLinuxHomeAsync(ctx, distro, user, ct);
        if (home is null)
            return StepResult.Fail("Could not resolve Linux home directory for openclaw user");

        // Resolve before baseline setup and pass the same absolute path to both
        // setup and injection. The managed gateway starts from this user's home,
        // so relative configured paths are home-relative rather than caller-cwd-relative.
        var workspace = await ResolveWorkspacePathAsync(ctx, distro, user, home, ct);
        if (string.IsNullOrWhiteSpace(workspace))
            return StepResult.Fail("Could not resolve OpenClaw agent workspace path");

        var workspaceOverride = ctx.Config.WindowsNodeContext.WorkspacePath?.Trim();
        var runBaselineSetup = !string.IsNullOrWhiteSpace(workspaceOverride);
        if (!runBaselineSetup)
        {
            var defaultWorkspace = await ResolveConfiguredDefaultWorkspacePathAsync(ctx, distro, user, home, ct);
            if (string.IsNullOrWhiteSpace(defaultWorkspace))
                return StepResult.Fail("Could not resolve OpenClaw default workspace path");

            runBaselineSetup = string.Equals(
                workspace.TrimEnd('/'),
                defaultWorkspace.TrimEnd('/'),
                StringComparison.Ordinal);
        }

        // Per-agent workspaces are already initialized by onboarding/agents add.
        // Running global setup for one would rewrite agents.defaults.workspace.
        if (runBaselineSetup)
        {
            var setupResult = await RunOpenclawSetupAsync(ctx, distro, user, workspace, ct);
            if (!setupResult.IsSuccess)
                return setupResult;
        }

        var target = new WindowsNodeContextTarget(distro, user, workspace);
        try
        {
            _currentTargetWasNew = await RecordAppliedTargetAsync(ctx, target, ct);
            _currentTarget = target;
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Could not persist Windows node context install state: {ex.Message}", ex);
        }

        var script = BuildApplyScript(workspace);
        // Uses stdin to bypass wsl.exe argv variable-expansion (see docs/WSL_EXE_ARGV_PITFALL.md).
        var result = await ctx.Commands.RunInWslAsync(distro, script, timeout, ct: ct, user: user, inputViaStdin: true);

        if (result.ExitCode != 0 || !result.Stdout.Contains("WINDOWS_NODE_CONTEXT_READY", StringComparison.Ordinal))
        {
            if (_currentTargetWasNew && result.ExitCode is 2 or 4)
            {
                try
                {
                    await RemoveRecordedTargetAsync(ctx, target, ct);
                    _currentTarget = null;
                    _currentTargetWasNew = false;
                }
                catch (Exception ex)
                {
                    return StepResult.Fail(
                        $"Windows node context injection failed and install-state cleanup also failed: {ex.Message}",
                        ex);
                }
            }

            return StepResult.Fail($"Windows node context injection failed (exit {result.ExitCode}): {FirstNonEmpty(result.Stderr, result.Stdout)}");
        }

        ctx.Logger.Info($"Windows node context injected into workspace: {workspace}");
        return StepResult.Ok("Windows node context injected");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, ctx.Config.WindowsNodeContext.TimeoutSeconds));
        var hasInstallState = File.Exists(InstallStatePath(ctx));
        WindowsNodeContextTarget[] targets;
        if (_currentTarget is { } current)
        {
            targets = [current];
        }
        else if (_executeAttempted)
        {
            // Failed-step rollback for an attempt that never modified a target.
            // Do not reinterpret this as a fresh uninstall of earlier installs.
            return;
        }
        else if (hasInstallState)
        {
            var state = await ReadInstallStateAsync(ctx, ct);
            targets = state.Targets.ToArray();
        }
        else
        {
            var legacyTarget = await ResolveLegacyUninstallTargetAsync(ctx, ct);
            targets = legacyTarget is null ? [] : [legacyTarget];
        }
        if (targets.Length == 0)
            return;

        var failures = new List<string>();
        foreach (var target in targets)
        {
            // Uses stdin to bypass wsl.exe argv variable-expansion (see docs/WSL_EXE_ARGV_PITFALL.md).
            var result = await ctx.Commands.RunInWslAsync(
                target.DistroName,
                BuildRollbackScript(target.WorkspacePath),
                timeout,
                ct: ct,
                user: target.User,
                inputViaStdin: true);

            if (result.ExitCode != 0 && !IsMissingDistroResult(result))
            {
                failures.Add(
                    $"{target.DistroName}:{target.WorkspacePath} (exit {result.ExitCode}): " +
                    FirstNonEmpty(result.Stderr, result.Stdout));
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("Windows node context cleanup failed: " + string.Join("; ", failures));

        if (_currentTarget is { } appliedTarget)
        {
            await RemoveRecordedTargetAsync(ctx, appliedTarget, ct);
        }
        else
        {
            File.Delete(InstallStatePath(ctx));
        }
    }

    private static async Task<WindowsNodeContextTarget?> ResolveLegacyUninstallTargetAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var distro = ctx.DistroName;
        if (string.IsNullOrWhiteSpace(distro))
            return null;
        var registered = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (registered.ExitCode != 0)
        {
            if (IsWslUnavailableResult(registered) || IsMissingDistroResult(registered))
                return null;
            throw new InvalidOperationException(
                "Could not inspect WSL distributions while cleaning legacy Windows node context: " +
                FirstNonEmpty(registered.Stderr, registered.Stdout));
        }

        if (!WslInstallSupport.ContainsDistro(registered.Stdout, distro))
            return null;


        var user = ctx.Config.Wsl.User;
        var (home, result) = await QueryLinuxHomeAsync(ctx, distro, user, ct);
        if (home is null)
        {
            if (IsMissingDistroResult(result))
                return null;
            throw new InvalidOperationException(
                "Could not resolve Linux home directory while cleaning legacy Windows node context: " +
                FirstNonEmpty(result.Stderr, result.Stdout));
        }

        var workspace = await ResolveWorkspacePathAsync(ctx, distro, user, home, ct);
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidOperationException("Could not resolve workspace while cleaning legacy Windows node context");

        return new WindowsNodeContextTarget(distro, user, workspace);
    }

    internal static string InstallStatePath(SetupContext ctx) =>
        Path.Combine(ctx.LocalDataDir, InstallStateFileName);

    internal static async Task<bool> RecordAppliedTargetAsync(
        SetupContext ctx,
        WindowsNodeContextTarget target,
        CancellationToken ct)
    {
        var state = await ReadInstallStateAsync(ctx, ct);
        var exists = state.Targets.Contains(target);
        if (exists)
            return false;

        state.Targets.Add(target);
        var json = JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(InstallStatePath(ctx), json, ct);
        return true;
    }

    internal static async Task<WindowsNodeContextInstallState> ReadInstallStateAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var path = InstallStatePath(ctx);
        if (!File.Exists(path))
            return new WindowsNodeContextInstallState();

        var json = await File.ReadAllTextAsync(path, ct);
        var state = JsonSerializer.Deserialize<WindowsNodeContextInstallState>(json, SetupConfig.JsonOptions)
            ?? throw new InvalidDataException("Windows node context install state is empty");
        if (state.Targets.Any(target =>
                string.IsNullOrWhiteSpace(target.DistroName) ||
                string.IsNullOrWhiteSpace(target.User) ||
                string.IsNullOrWhiteSpace(target.WorkspacePath) ||
                !target.WorkspacePath.StartsWith('/')))
        {
            throw new InvalidDataException("Windows node context install state contains an invalid target");
        }

        return state;
    }

    private static async Task RemoveRecordedTargetAsync(
        SetupContext ctx,
        WindowsNodeContextTarget target,
        CancellationToken ct)
    {
        var state = await ReadInstallStateAsync(ctx, ct);
        state.Targets.RemoveAll(candidate => candidate == target);
        if (state.Targets.Count == 0)
        {
            File.Delete(InstallStatePath(ctx));
            return;
        }

        var json = JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(InstallStatePath(ctx), json, ct);
    }

    internal static bool IsMissingDistroResult(CommandResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = string.Concat(result.Stderr, '\n', result.Stdout);

        return output.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    // A conclusive "WSL is unavailable" answer still proves no app-owned distro can
    // hold legacy Windows node context, so uninstall has nothing to clean. Only an
    // ambiguous inspection failure (timeout, access denied, empty output) stays an
    // explicit error. Matches ExistingConfigDetector.InterpretDistroList and the
    // lenient uninstall behavior in StartGatewayStep and CleanupStaleDistroStep.
    internal static bool IsWslUnavailableResult(CommandResult result)
        => result.ExitCode != 0
            && (WslViabilityInspector.LooksUnavailable(result)
                || result.Stderr.Contains("Failed to start process", StringComparison.Ordinal));

    internal static async Task<string?> ResolveLinuxHomeAsync(SetupContext ctx, string distro, string user, CancellationToken ct)
    {
        var (home, _) = await QueryLinuxHomeAsync(ctx, distro, user, ct);
        return home;
    }

    internal static async Task<(string? Home, CommandResult Result)> QueryLinuxHomeAsync(
        SetupContext ctx,
        string distro,
        string user,
        CancellationToken ct)
    {
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            "getent passwd \"$(id -un)\" | cut -d: -f6",
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user);

        if (result.ExitCode != 0)
            return (null, result);

        var home = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && line.StartsWith('/'));

        return (string.IsNullOrWhiteSpace(home) ? null : home, result);
    }

    internal static async Task<StepResult> RunOpenclawSetupAsync(SetupContext ctx, string distro, string user, string workspaceAbsolute, CancellationToken ct)
    {
        var workspaceArg = WslShellQuoting.QuotePosixSingleQuote(workspaceAbsolute);

        // Validated Gateway releases span both setup contracts. Detect the
        // installed exact release's contract instead of keying behavior by tag.
        var script = $"""
            set -e
            {ctx.WslPathPrefix}
            if openclaw setup --help 2>&1 | grep -q -- '--baseline'; then
                openclaw setup --baseline --workspace {workspaceArg} >/dev/null
            else
                openclaw setup --workspace {workspaceArg} >/dev/null
            fi
            """;
        // Uses stdin to bypass wsl.exe argv variable-expansion (the script's
        // PATH prefix references $PATH, which would be expanded to the
        // Windows PATH on the argv path). See docs/WSL_EXE_ARGV_PITFALL.md.
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(Math.Max(30, ctx.Config.WindowsNodeContext.TimeoutSeconds / 2)),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.ExitCode != 0)
            return StepResult.Fail($"openclaw setup failed (exit {result.ExitCode}): {FirstNonEmpty(result.Stderr, result.Stdout)}");

        return StepResult.Ok();
    }

    internal static async Task<string?> ResolveWorkspacePathAsync(SetupContext ctx, string distro, string user, string home, CancellationToken ct)
    {
        var workspaceOverride = ctx.Config.WindowsNodeContext.WorkspacePath?.Trim();
        if (!string.IsNullOrWhiteSpace(workspaceOverride))
            return ExpandLinuxPath(workspaceOverride, home);

        // `agents list` resolves per-agent overrides and returns the effective
        // workspace used by the default/main chat agent.
        var script = $"{ctx.WslPathPrefix}\nopenclaw agents list --json";
        // Uses stdin to bypass wsl.exe argv variable-expansion (the script's
        // PATH prefix references $PATH). See docs/WSL_EXE_ARGV_PITFALL.md.
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.TimedOut || result.ExitCode != 0)
            return null;

        var raw = ExtractDefaultAgentWorkspaceFromAgentsOutput(result.Stdout);
        return string.IsNullOrWhiteSpace(raw) ? null : ExpandLinuxPath(raw, home);
    }

    internal static async Task<string?> ResolveConfiguredDefaultWorkspacePathAsync(
        SetupContext ctx,
        string distro,
        string user,
        string home,
        CancellationToken ct)
    {
        var script = $"{ctx.WslPathPrefix}\nopenclaw config get agents.defaults.workspace --json";
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.TimedOut)
            return null;

        var raw = ExtractWorkspaceFromConfigOutput(result.Stdout);
        if (result.ExitCode != 0)
        {
            // Validated releases report an absent key with exit 1. Only that
            // known case may select the default; other read failures must not
            // be persisted by the subsequent `setup --workspace` call.
            if (!result.Stderr.Contains(
                    "Config path not found: agents.defaults.workspace",
                    StringComparison.Ordinal))
                return null;

            raw = $"{home.TrimEnd('/')}/.openclaw/workspace";
        }
        else if (string.IsNullOrWhiteSpace(raw))
        {
            // A present JSON null uses OpenClaw's default. Empty or malformed
            // successful output is an operational failure, not evidence that
            // the key is absent.
            if (!result.Stdout
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => string.Equals(line.Trim(), "null", StringComparison.Ordinal)))
                return null;

            raw = $"{home.TrimEnd('/')}/.openclaw/workspace";
        }

        return ExpandLinuxPath(raw, home);
    }

    internal static string? ExtractDefaultAgentWorkspaceFromAgentsOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith('['))
                continue;

            var candidate = string.Join('\n', lines.Skip(i));
            var end = candidate.LastIndexOf(']');
            if (end < 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(candidate[..(end + 1)]);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    continue;

                JsonElement? main = null;
                foreach (var agent in document.RootElement.EnumerateArray())
                {
                    if (agent.ValueKind != JsonValueKind.Object)
                        continue;

                    if (agent.TryGetProperty("isDefault", out var isDefault) &&
                        isDefault.ValueKind == JsonValueKind.True)
                    {
                        main = agent;
                        break;
                    }

                    if (main is null &&
                        agent.TryGetProperty("id", out var id) &&
                        string.Equals(id.GetString(), "main", StringComparison.OrdinalIgnoreCase))
                        main = agent;
                }

                if (main is { } selected &&
                    selected.TryGetProperty("workspace", out var workspace) &&
                    workspace.ValueKind == JsonValueKind.String)
                    return workspace.GetString();
            }
            catch (JsonException)
            {
                // Keep scanning in case a warning line started with '['.
            }
        }

        return null;
    }

    internal static string? ExtractWorkspaceFromConfigOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        // openclaw config get --json prints a JSON value; warnings may be on stderr (suppressed)
        // or as banner lines on stdout. Walk lines from bottom to find a usable value.
        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var candidate = lines[i];
            // Try JSON string parse first
            if (candidate.StartsWith('"') && candidate.EndsWith('"'))
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<string>(candidate);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
            }

            if (candidate == "null")
                continue;

            // Plain string (non-JSON output)
            if (candidate.StartsWith('/') || candidate.StartsWith('~'))
                return candidate;
        }

        return null;
    }

    internal static string ExpandLinuxPath(string path, string home)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "null" || trimmed == "undefined")
            return $"{home.TrimEnd('/')}/.openclaw/workspace";

        if (trimmed == "~")
            return home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            return $"{home.TrimEnd('/')}/{trimmed[2..]}";
        if (trimmed.StartsWith('/'))
            return trimmed;
        return $"{home.TrimEnd('/')}/{trimmed}";
    }

    internal static string BuildApplyScript(string absoluteWorkspacePath)
        => $$"""
            set -e
            set -o pipefail
            workspace={{WslShellQuoting.QuotePosixSingleQuote(absoluteWorkspacePath)}}
            agents="$workspace/AGENTS.md"
            block_b64={{WslShellQuoting.QuotePosixSingleQuote(ManagedBlockBase64())}}
            begin_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.BeginMarker)}}
            end_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.EndMarker)}}
            if [ -L "$agents" ]; then
                echo "AGENTS_SYMLINK:$agents" >&2
                exit 2
            fi
            if [ ! -f "$agents" ]; then
                mkdir -p "$workspace"
                : > "$agents"
                echo "WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK:$agents"
            fi
            begin_count=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            end_count=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            if [ "$begin_count" -gt 1 ] || [ "$end_count" -gt 1 ] || [ "$begin_count" != "$end_count" ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            if [ "$begin_count" = 1 ]; then
                begin_line=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
                end_line=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
                if [ "$end_line" -lt "$begin_line" ]; then
                    echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                    exit 4
                fi
            fi
            tmp=$(mktemp "$workspace/.AGENTS.md.openclaw.XXXXXX")
            trap 'rm -f -- "$tmp"' EXIT
            awk -v BEGIN_M="$begin_marker" -v END_M="$end_marker" '
              BEGIN { in_block = 0 }
              { marker_line = $0; sub(/\r$/, "", marker_line) }
              marker_line == BEGIN_M { in_block = 1; next }
              in_block && marker_line == END_M { in_block = 0; next }
              in_block { next }
              /^[[:space:]]*$/ { blank = blank $0 ORS; next }
              { printf "%s%s%s", blank, $0, ORS; blank = "" }
            ' "$agents" > "$tmp"
            if [ -s "$tmp" ]; then
                printf '\n' >> "$tmp"
            fi
            printf '%s' "$block_b64" | base64 -d >> "$tmp"
            printf '\n' >> "$tmp"
            chmod --reference="$agents" "$tmp"
            mv -- "$tmp" "$agents"
            trap - EXIT
            echo "WINDOWS_NODE_CONTEXT_WORKSPACE:$workspace"
            echo "WINDOWS_NODE_CONTEXT_READY"
            """;

    internal static string BuildRollbackScript(string absoluteWorkspacePath)
        => $$"""
            set -e
            set -o pipefail
            workspace={{WslShellQuoting.QuotePosixSingleQuote(absoluteWorkspacePath)}}
            agents="$workspace/AGENTS.md"
            begin_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.BeginMarker)}}
            end_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.EndMarker)}}
            if [ ! -e "$agents" ]; then
                echo "WINDOWS_NODE_CONTEXT_ABSENT"
                exit 0
            fi
            if [ -L "$agents" ]; then
                echo "AGENTS_SYMLINK_ROLLBACK_SKIPPED:$agents"
                exit 5
            fi
            begin_count=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            end_count=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            if [ "$begin_count" = 0 ] && [ "$end_count" = 0 ]; then
                echo "WINDOWS_NODE_CONTEXT_REMOVED"
                exit 0
            fi
            if [ "$begin_count" != 1 ] || [ "$end_count" != 1 ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            begin_line=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
            end_line=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
            if [ "$end_line" -lt "$begin_line" ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            tmp=$(mktemp "$workspace/.AGENTS.md.openclaw.XXXXXX")
            trap 'rm -f -- "$tmp"' EXIT
            awk -v BEGIN_M="$begin_marker" -v END_M="$end_marker" '
              BEGIN { in_block = 0 }
              { marker_line = $0; sub(/\r$/, "", marker_line) }
              marker_line == BEGIN_M { in_block = 1; next }
              in_block && marker_line == END_M { in_block = 0; next }
              in_block { next }
              { print }
            ' "$agents" > "$tmp"
            chmod --reference="$agents" "$tmp"
            mv -- "$tmp" "$agents"
            trap - EXIT
            echo "WINDOWS_NODE_CONTEXT_REMOVED"
            """;

    private static string ManagedBlockBase64()
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(WindowsNodeContextSection.ManagedBlock));

    private static string FirstNonEmpty(params string[] values)
        => values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? "no output";

    private static string? ReadMarkerValue(string output, string marker)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))
            ?[marker.Length..];
}

using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using OpenClaw.Shared.Commands;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Pure function: <see cref="SandboxExecutionRequest"/> + scratch directory →
/// <see cref="MxcConfig"/> for direct invocation of <c>wxc-exec.exe</c>.
/// </summary>
/// <remarks>
/// What this class does:
/// <list type="bullet">
/// <item>Translates <see cref="SandboxPolicy"/> (from the Sandbox page) and the
///   agent's request into the JSON shape wxc-exec consumes.</item>
/// <item><see cref="ResolvePathDirsForShellPath"/> — reconstructs a bounded
///   <c>PATH</c> inside the launched shell and grants backend-safe PATH
///   directories as readonly, so user-level tools can be resolved and executed
///   without asking MXC's DACL fallback to prepare protected system directories.</item>
/// <item>Scratch dir injection — adds the per-invocation scratch dir as
///   readwrite and bootstraps <c>TEMP</c>/<c>TMP</c>/<c>TMPDIR</c> inside the
///   launched shell. Explicit <c>process.env</c> injection is intentionally
///   disabled for the current Windows MXC 0.7 processcontainer backend because
///   non-empty env entries fail process creation.</item>
/// <item>Cwd handling — defaults omitted cwd to the writable per-run scratch
///   directory, and adds an explicit request cwd as readonly when not already
///   covered by an allow grant. AppContainer does NOT auto-grant cwd.</item>
/// <item>Defensive re-filter of allow lists against the deny list.</item>
/// <item>Command-line construction: direct argv uses Win32-reversible escaping;
///   legacy commands use cmd <c>/S /C</c> or powershell
///   <c>-EncodedCommand</c>.</item>
/// </list>
/// Env scrubbing happens upstream in <c>SystemCapability.HandleRunAsync</c>
/// via <c>ExecEnvSanitizer.Sanitize</c>; this class rejects explicit env until
/// the backend accepts it.
/// </remarks>
public static class MxcConfigBuilder
{
    // MXC processcontainer defaults to cmd because it starts inside the
    // AppContainer while preserving the default UI-deny boundary. PowerShell
    // remains available when explicitly requested, but callers must supply a
    // policy with AllowWindows=true because MXC 0.7 requires UI access for
    // PowerShell startup.
    private const string DefaultShell = "cmd";

    /// <summary>
    /// Default per-process timeout when the caller doesn't supply one.
    /// </summary>
    public const int DefaultProcessTimeoutMs = 30_000;

    /// <summary>
    /// Build the <see cref="MxcConfig"/> for a sandboxed invocation.
    /// </summary>
    /// <param name="request">Capability invocation request.</param>
    /// <param name="scratchDir">Per-invocation scratch directory the executor created.</param>
    public static MxcConfig Build(
        SandboxExecutionRequest request,
        string scratchDir) =>
        Build(request, scratchDir, MxcConfigBuildContext.Default);

    internal static MxcConfig Build(
        SandboxExecutionRequest request,
        string scratchDir,
        MxcConfigBuildContext context)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(scratchDir)) throw new ArgumentException("scratchDir required", nameof(scratchDir));
        if (context is null) throw new ArgumentNullException(nameof(context));
        var readonlyGrantIsBackendSafe = context.ReadonlyGrantIsBackendSafe ?? IsBackendSafeReadonlyGrant;

        var policy = request.Policy;
        var workingDirectory = string.IsNullOrWhiteSpace(request.Cwd) ? scratchDir : request.Cwd;
        var args = ParseSystemRunArgs(request.Args);
        string? shell = null;
        if (args.DirectArgv is null)
        {
            shell = NormalizeSupportedShell(args.Shell);
        }

        var requiresWindowsUi = args.DirectArgv is { Count: > 0 }
            ? IsPowerShellFamilyExecutable(args.DirectArgv[0])
            : IsPowerShellFamilyShell(shell!);
        if (requiresWindowsUi && policy?.Ui?.AllowWindows != true)
        {
            throw new NotSupportedException(
                "PowerShell-family shells require UI access with the Windows MXC 0.7 processcontainer backend.");
        }

        if (request.Env is { Count: > 0 })
        {
            throw new NotSupportedException(
                "Explicit environment variables are not supported by the Windows MXC 0.7 processcontainer backend.");
        }

        // readonly = UI grants. Additional compatibility paths are added below.
        // PATH itself is bootstrapped inside the shell, and backend-safe PATH
        // directories are also granted readonly so PATH-resolved user tools can
        // actually be read/executed from inside AppContainer.
        var roFromPolicy = (policy?.Filesystem?.ReadonlyPaths ?? Array.Empty<string>()).ToList();
        var pathDirs = ResolvePathDirsForShellPath(context.PathEnvVar);
        foreach (var dir in pathDirs)
        {
            if (!readonlyGrantIsBackendSafe(dir)) continue;
            if (!roFromPolicy.Contains(dir, StringComparer.OrdinalIgnoreCase))
                roFromPolicy.Add(dir);
        }

        // Normal direct argv is passed straight to CreateProcessW with reversible
        // Win32 escaping. cmd.exe /C is special: cmd parses its raw command line
        // instead of CommandLineToArgvW, so the canonical gateway wrapper must use
        // the cmd-aware serializer. It also carries the PATH/temp bootstrap because
        // MXC 0.7 rejects non-empty process.env.
        var commandLine = args.DirectArgv is not null
            ? BuildDirectArgvCommandLine(args.DirectArgv, scratchDir, pathDirs)
            : ShellCommandLine.Build(shell!, args.Command, args.Arguments, scratchDir, pathDirs);
        var allowWindows = policy?.Ui?.AllowWindows == true;

        // readwrite = UI grants + scratch dir.
        var rwFromPolicy = (policy?.Filesystem?.ReadwritePaths ?? Array.Empty<string>()).ToList();
        if (!rwFromPolicy.Contains(scratchDir, StringComparer.OrdinalIgnoreCase))
            rwFromPolicy.Add(scratchDir);

        // denied list from policy (settings dir, ~/.ssh, browser profiles, ...).
        // Keep the full list for local allow-list filtering, but do not emit
        // filesystem.deniedPaths to wxc-exec. Windows MXC 0.7 rejects that field;
        // omitted grants remain denied by default inside the AppContainer.
        var deniedForFiltering = (policy?.Filesystem?.DeniedPaths ?? Array.Empty<string>()).ToList();
        string[]? deniedForBackend = null;

        // cwd auto-grant — AppContainer does not auto-grant the working
        // directory. Give ungranted cwd read access so shells can start, but
        // never silently upgrade it to write access; writes require an
        // explicit readwrite folder grant.
        if (!string.IsNullOrWhiteSpace(request.Cwd)
            && !IsCoveredBy(request.Cwd, roFromPolicy)
            && !IsCoveredBy(request.Cwd, rwFromPolicy))
        {
            if (!OverlapsAny(request.Cwd, deniedForFiltering))
                roFromPolicy.Add(request.Cwd);
        }

        // Deny wins: strip any allow that overlaps a deny after the merges above.
        roFromPolicy = FilterOutDenied(roFromPolicy, deniedForFiltering);
        rwFromPolicy = FilterOutDenied(rwFromPolicy, deniedForFiltering);

        // process.env — intentionally empty. MXC 0.7 processcontainer currently
        // fails process creation when a non-empty process.env array is supplied,
        // so shell-level bootstrap above carries PATH/scratch temp instead.
        var env = BuildEnv(request.Env);

        // timeout — caller-supplied or default.
        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : DefaultProcessTimeoutMs;

        // capabilities — only network for now.
        var capabilities = new List<string>();
        if (policy?.Network?.AllowOutbound == true)
            capabilities.Add("internetClient");

        var network = new MxcNetwork
        {
            DefaultPolicy = policy?.Network?.AllowOutbound == true ? "allow" : "block",
            EnforcementMode = "capabilities",
        };

        var topLevelUi = new MxcUi
        {
            Disable = !allowWindows,
            Clipboard = MapClipboard(policy?.Ui?.Clipboard ?? ClipboardPolicy.None),
            Injection = false,
        };

        var processContainerUi = new MxcBaseProcessUi
        {
            Isolation = allowWindows ? "desktop" : "container",
            DesktopSystemControl = false,
            SystemSettings = "none",
            Ime = false,
        };

        return new MxcConfig
        {
            Version = MxcPolicyBuilder.SupportedPolicyVersion,
            ContainerId = context.ContainerId ?? Guid.NewGuid().ToString("N"),
            // Top-level "containment" is intentionally omitted; the SDK doesn't
            // emit it either. Isolation lives in processContainer.ui.isolation.
            Process = new MxcProcess
            {
                CommandLine = commandLine,
                Cwd = workingDirectory,
                Env = env,
                TimeoutMs = timeoutMs,
            },
            ProcessContainer = new MxcProcessContainer
            {
                LeastPrivilege = false,
                Capabilities = capabilities.ToArray(),
                Ui = processContainerUi,
            },
            Filesystem = new MxcFilesystem
            {
                ReadonlyPaths = roFromPolicy.ToArray(),
                ReadwritePaths = rwFromPolicy.ToArray(),
                DeniedPaths = deniedForBackend,
                // SDK output didn't include clearPolicyOnExit even when the
                // input policy had it set, so we omit it here too.
                ClearPolicyOnExit = null,
            },
            Network = network,
            Ui = topLevelUi,
            Lifecycle = new MxcLifecycle
            {
                DestroyOnExit = true,
                PreservePolicy = false,
            },
        };
    }

    /// <summary>
    /// Walk PATH and return each existing directory for shell-level PATH
    /// bootstrap. Drive roots (e.g. <c>C:\</c>) are skipped so a misconfigured
    /// PATH entry cannot make the payload search an entire drive root.
    /// </summary>
    public static List<string> ResolvePathDirsForShellPath(string? pathEnvVar = null)
    {
        var path = pathEnvVar ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0)
            .ToList();

        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in pathDirs)
        {
            if (IsDriveRoot(dir)) continue;
            try
            {
                if (!Directory.Exists(dir)) continue;
            }
            catch
            {
                continue;
            }
            if (seen.Add(dir)) dirs.Add(dir);
        }

        return dirs;
    }

    private static bool IsDriveRoot(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) return false;
            var trimmedDir = Path.TrimEndingDirectorySeparator(dir);
            var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
            return string.Equals(trimmedDir, trimmedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBackendSafeReadonlyGrant(string dir)
    {
        if (IsDriveRoot(dir)) return false;
        if (IsProtectedSystemPath(dir)) return false;
        if (!CanMxcDaclFallbackPreparePath(dir)) return false;
        return true;
    }

    private static bool CanMxcDaclFallbackPreparePath(string dir)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        return CanMxcDaclFallbackPreparePathWindows(dir);
    }

    [SupportedOSPlatform("windows")]
    private static bool CanMxcDaclFallbackPreparePathWindows(string dir)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null)
                return false;

            var tokenSids = new HashSet<SecurityIdentifier> { identity.User };
            if (identity.Groups is not null)
            {
                foreach (var group in identity.Groups.OfType<SecurityIdentifier>())
                    tokenSids.Add(group);
            }

            var rules = new DirectoryInfo(dir)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

            return HasDirectUserChangePermissions(
                rules.OfType<FileSystemAccessRule>(),
                identity.User,
                tokenSids.Contains);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool HasDirectUserChangePermissions(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier userSid,
        Func<SecurityIdentifier, bool> isTokenSid)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(userSid);
        ArgumentNullException.ThrowIfNull(isTokenSid);

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid)
                continue;

            if ((rule.FileSystemRights & FileSystemRights.ChangePermissions) == 0)
                continue;

            var isUser = sid.Equals(userSid);
            if (rule.AccessControlType == AccessControlType.Deny && (isUser || isTokenSid(sid)))
                return false;

            // MXC's DACL fallback runs under a restricted token. An admin-group
            // allow on the host does not grant that token WRITE_DAC.
            if (rule.AccessControlType == AccessControlType.Allow && isUser)
                allowed = true;
        }

        return allowed;
    }

    private static bool IsProtectedSystemPath(string dir)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var normalized = NormalizePath(dir);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var root in ProtectedSystemRoots())
        {
            var protectedRoot = NormalizePath(root);
            if (!string.IsNullOrWhiteSpace(protectedRoot) &&
                IsSameOrNested(normalized, protectedRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ProtectedSystemRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("windir") ?? string.Empty;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
    }

    /// <summary>
    /// Build the explicit process.env array for the wxc-exec sandbox.
    /// </summary>
    /// <remarks>
    /// Current MXC 0.7 Windows processcontainer accepts an empty env array but
    /// rejects non-empty entries at <c>CreateProcessW</c>. Emit an explicit
    /// empty array for normal requests so the config does not rely on implicit
    /// host-environment inheritance semantics. PATH and scratch temp variables
    /// are set by the shell command line bootstrap instead.
    /// </remarks>
    public static IReadOnlyList<string>? BuildEnv(IReadOnlyDictionary<string, string>? requestEnv)
    {
        if (requestEnv is null || requestEnv.Count == 0)
            return Array.Empty<string>();

        throw new NotSupportedException(
            "Explicit environment variables are not supported by the Windows MXC 0.7 processcontainer backend.");
    }

    private static List<string> FilterOutDenied(List<string> allowed, List<string> denied)
    {
        if (allowed.Count == 0 || denied.Count == 0) return allowed;
        var normalizedDenied = denied
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return allowed
            .Where(a =>
            {
                var na = NormalizePath(a);
                if (string.IsNullOrEmpty(na)) return false;
                foreach (var d in normalizedDenied)
                    if (PathsOverlap(na, d)) return false;
                return true;
            })
            .ToList();
    }

    internal static MxcConfig WithNonCascadingVolumeRootGrants(MxcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Filesystem is null)
            return config;

        var readonlyPaths = (config.Filesystem.ReadonlyPaths ?? Array.Empty<string>()).ToList();
        var readwritePaths = config.Filesystem.ReadwritePaths ?? Array.Empty<string>();
        var roots = readonlyPaths
            .Concat(readwritePaths)
            .Select(TryGetWindowsDriveRoot)
            .Where(root => root is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in roots)
        {
            if (!readonlyPaths.Contains(root, StringComparer.OrdinalIgnoreCase) &&
                !readwritePaths.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                readonlyPaths.Add(root);
            }
        }

        return config with
        {
            Filesystem = config.Filesystem with
            {
                ReadonlyPaths = readonlyPaths.ToArray(),
            },
        };
    }

    private static string? TryGetWindowsDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/'))
        {
            return null;
        }

        return $"{char.ToUpperInvariant(path[0])}:\\";
    }

    private static bool IsCoveredBy(string candidate, IEnumerable<string> ancestors)
    {
        var nc = NormalizePath(candidate);
        if (string.IsNullOrEmpty(nc)) return false;
        foreach (var a in ancestors)
        {
            var na = NormalizePath(a);
            if (string.IsNullOrEmpty(na)) continue;
            if (IsSameOrNested(nc, na)) return true;
        }
        return false;
    }

    private static bool OverlapsAny(string candidate, IEnumerable<string> paths)
    {
        var nc = NormalizePath(candidate);
        if (string.IsNullOrEmpty(nc)) return false;
        foreach (var path in paths)
        {
            var np = NormalizePath(path);
            if (string.IsNullOrEmpty(np)) continue;
            if (PathsOverlap(nc, np)) return true;
        }
        return false;
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrNested(left, right) || IsSameOrNested(right, left);

    private static bool IsSameOrNested(string path, string candidateParent)
    {
        if (string.Equals(path, candidateParent, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(candidateParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(candidateParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static string MapClipboard(ClipboardPolicy mode) => mode switch
    {
        ClipboardPolicy.Read => "read",
        ClipboardPolicy.Write => "write",
        ClipboardPolicy.All => "all",
        _ => "none",
    };

    private static string BuildDirectArgvCommandLine(
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        if (!CanonicalCmdCarrier.IsCmdExecutable(argv.Count > 0 ? argv[0] : null))
            return DirectArgvCommandLine.Build(argv);

        if (!SelectsCmdCommandMode(argv))
            return DirectArgvCommandLine.Build(argv);

        if (!CanonicalCmdCarrier.TryGetCanonicalPayload(argv, out var payload))
        {
            throw new NotSupportedException(
                "Direct cmd.exe command wrappers must use canonical argv: cmd.exe /d /s /c <command>.");
        }

        return ShellCommandLine.BuildCanonicalCmdWrapper(
            argv[0],
            payload,
            scratchDir,
            pathDirs);
    }

    private static bool SelectsCmdCommandMode(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var argument = argv[i].Trim();
            if (!argument.StartsWith("/", StringComparison.Ordinal))
                continue;

            if (argument.StartsWith("/c", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("/k", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("/c", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("/k", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Capability args envelope for system.run. Other capability shapes can add
    /// their own parser here as they're rehosted.
    /// </summary>
    private static SystemRunArgs ParseSystemRunArgs(System.Text.Json.JsonElement args)
    {
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object)
            return new SystemRunArgs("", DefaultShell, Array.Empty<string>(), null);

        string command = args.TryGetProperty("command", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? (c.GetString() ?? "") : "";
        string shell = args.TryGetProperty("shell", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
            ? (s.GetString() ?? DefaultShell) : DefaultShell;
        string[] arguments = Array.Empty<string>();
        if (args.TryGetProperty("args", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            arguments = a.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToArray();
        }

        string[]? directArgv = null;
        if (args.TryGetProperty("argv", out var direct)
            && direct.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            if (direct.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new NotSupportedException("Direct argv must be an array of strings.");

            directArgv = direct.EnumerateArray()
                .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String
                    ? e.GetString()!
                    : throw new NotSupportedException("Direct argv entries must be strings."))
                .ToArray();
        }

        return new SystemRunArgs(command, shell, arguments, directArgv);
    }

    private static bool IsPowerShellFamilyShell(string shell)
    {
        var normalized = shell.Trim().ToLowerInvariant();
        return normalized is "powershell" or "pwsh";
    }

    private static bool IsPowerShellFamilyExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        var fileName = GetWindowsExecutableFileName(executable);
        var stem = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
        return IsPowerShellFamilyShell(stem);
    }

    private static string GetWindowsExecutableFileName(string executable)
    {
        var normalized = executable.Trim().Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string NormalizeSupportedShell(string shell)
    {
        var normalized = string.IsNullOrWhiteSpace(shell)
            ? DefaultShell
            : shell.Trim().ToLowerInvariant();
        return normalized switch
        {
            "cmd" or "powershell" or "pwsh" => normalized,
            _ => throw new NotSupportedException(
                $"Unsupported shell '{shell}' for the Windows MXC 0.7 processcontainer backend."),
        };
    }

    private sealed record SystemRunArgs(
        string Command,
        string Shell,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string>? DirectArgv);
}

internal sealed record MxcConfigBuildContext(
    string? ContainerId = null,
    string? PathEnvVar = null,
    Func<string, bool>? ReadonlyGrantIsBackendSafe = null)
{
    public static MxcConfigBuildContext Default { get; } = new();
}

/// <summary>
/// Builds a Windows process command line whose arguments round-trip through
/// <c>CommandLineToArgvW</c> without a shell parsing the approved argv.
/// </summary>
internal static class DirectArgvCommandLine
{
    private static readonly char[] QuoteRequiredChars = [' ', '\t', '\n', '\v', '"'];

    public static string Build(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
            throw new ArgumentException("Direct argv requires an executable.", nameof(argv));

        var commandLine = new StringBuilder();
        for (var i = 0; i < argv.Count; i++)
        {
            var argument = argv[i]
                ?? throw new ArgumentException("Direct argv entries cannot be null.", nameof(argv));
            if (argument.Contains('\0'))
                throw new ArgumentException("Direct argv entries cannot contain NUL.", nameof(argv));

            if (i > 0)
                commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        return commandLine.ToString();
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(QuoteRequiredChars) < 0)
        {
            commandLine.Append(argument);
            return;
        }

        commandLine.Append('"');
        var index = 0;
        while (index < argument.Length)
        {
            var backslashStart = index;
            while (index < argument.Length && argument[index] == '\\')
                index++;

            var backslashCount = index - backslashStart;
            if (index == argument.Length)
            {
                commandLine.Append('\\', backslashCount * 2);
                break;
            }

            if (argument[index] == '"')
                commandLine.Append('\\', backslashCount * 2 + 1);
            else
                commandLine.Append('\\', backslashCount);

            commandLine.Append(argument[index]);
            index++;
        }

        commandLine.Append('"');
    }
}

/// <summary>
/// Shell command-line construction for the sandboxed payload — wraps the
/// agent's command in <c>cmd.exe /S /C "..."</c> or
/// <c>powershell.exe -EncodedCommand &lt;utf16le-base64&gt;</c> so it can be
/// passed verbatim to <c>CreateProcessW</c> inside the AppContainer.
/// </summary>
internal static class ShellCommandLine
{
    private const int MaxShellBootstrapPathChars = 4096;
    private static readonly string[] CmdBootstrapTempEnvNames = ["TEMP", "TMP", "TMPDIR"];

    public static string Build(
        string shell,
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        var normalized = (shell ?? "cmd").Trim().ToLowerInvariant();
        var bootstrapPathDirs = LimitPathDirsForCommandLine(pathDirs);
        return normalized switch
        {
            "cmd" => BuildCmd(command, argv, scratchDir, bootstrapPathDirs),
            "pwsh" or "powershell" => BuildPowershell(
                normalized == "pwsh" ? ResolvePwshExe(pathDirs) : ResolveWindowsPowerShellExe(),
                command,
                argv,
                scratchDir,
                bootstrapPathDirs),
            _ => throw new NotSupportedException(
                $"Unsupported shell '{shell}' for the Windows MXC 0.7 processcontainer backend."),
        };
    }

    public static string BuildCanonicalCmdWrapper(
        string executable,
        string command,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Canonical cmd wrapper requires an executable.", nameof(executable));

        return BuildCmd(
            command,
            Array.Empty<string>(),
            scratchDir,
            LimitPathDirsForCommandLine(pathDirs),
            executable);
    }

    private static IReadOnlyList<string> LimitPathDirsForCommandLine(IReadOnlyList<string> pathDirs)
    {
        if (pathDirs.Count == 0)
            return Array.Empty<string>();

        var bounded = new List<string>();
        var currentLength = 0;
        foreach (var dir in pathDirs)
        {
            var additionalLength = dir.Length + (bounded.Count == 0 ? 0 : 1);
            if (currentLength + additionalLength > MaxShellBootstrapPathChars)
                break;

            bounded.Add(dir);
            currentLength += additionalLength;
        }

        return bounded;
    }

    private static string BuildCmd(
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs,
        string? executable = null)
    {
        ThrowIfCmdContainsLineBreak(command, nameof(command));
        foreach (var arg in argv)
            ThrowIfCmdContainsLineBreak(arg, "argv");
        var containsDelayedExpansionSyntax =
            command.Contains('!') || argv.Any(arg => arg.Contains('!'));
        var bootstrapContainsDelayedExpansionSyntax =
            scratchDir.Contains('!') || pathDirs.Any(path => path.Contains('!'));

        // cmd /S /C "<command> [args]" — /S strips outer quotes so cmd treats
        // everything after /C as the command line verbatim. If the payload
        // references env vars we bootstrap in this same /C line, rewrite just
        // those refs to delayed expansion; otherwise cmd expands %TEMP% before
        // the preceding set command runs.
        var rewrittenCommand = RewriteCmdBootstrapEnvRefs(command, pathDirs, out var needsDelayedExpansion);
        var rewrittenArgv = new List<string>(argv.Count);
        foreach (var arg in argv)
        {
            rewrittenArgv.Add(RewriteCmdBootstrapEnvRefs(arg, pathDirs, out var argNeedsDelayedExpansion));
            needsDelayedExpansion |= argNeedsDelayedExpansion;
        }
        if (needsDelayedExpansion
            && (containsDelayedExpansionSyntax || bootstrapContainsDelayedExpansionSyntax))
        {
            throw new NotSupportedException(
                "cmd payloads cannot combine MXC bootstrap environment references with '!' delayed-expansion syntax in the payload or bootstrap paths.");
        }

        var sb = new StringBuilder(QuoteProcessPath(executable ?? ResolveCmdExe()));
        if (needsDelayedExpansion)
            sb.Append(" /V:ON");
        sb.Append(" /D /S /C \"");
        AppendCmdEnvironmentBootstrap(sb, scratchDir, pathDirs);
        sb.Append(rewrittenCommand);
        foreach (var a in rewrittenArgv)
        {
            sb.Append(' ');
            sb.Append(QuoteForCmd(a));
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void ThrowIfCmdContainsLineBreak(string value, string fieldName)
    {
        if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new NotSupportedException(
                $"cmd shell {fieldName} values cannot contain CR or LF characters with the Windows MXC 0.7 processcontainer backend.");
        }
    }

    private static string RewriteCmdBootstrapEnvRefs(
        string value,
        IReadOnlyList<string> pathDirs,
        out bool rewritten)
    {
        rewritten = false;
        var result = value;
        foreach (var name in CmdBootstrapTempEnvNames)
        {
            result = ReplaceOrdinalIgnoreCase(
                result,
                $"%{name}%",
                $"!{name}!",
                ref rewritten);
        }

        if (pathDirs.Count > 0)
        {
            result = ReplaceOrdinalIgnoreCase(
                result,
                "%PATH%",
                "!PATH!",
                ref rewritten);
        }

        return result;
    }

    private static string ReplaceOrdinalIgnoreCase(
        string value,
        string search,
        string replacement,
        ref bool replaced)
    {
        var index = value.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return value;

        var sb = new StringBuilder(value.Length);
        var cursor = 0;
        while (index >= 0)
        {
            sb.Append(value, cursor, index - cursor);
            sb.Append(replacement);
            cursor = index + search.Length;
            index = value.IndexOf(search, cursor, StringComparison.OrdinalIgnoreCase);
            replaced = true;
        }

        sb.Append(value, cursor, value.Length - cursor);
        return sb.ToString();
    }

    private static string BuildPowershell(
        string exe,
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        // -EncodedCommand <UTF-16LE-base64> avoids quoting pitfalls entirely.
        // We concatenate command + argv with spaces and let powershell parse it.
        var sb = new StringBuilder();
        AppendPowershellEnvironmentBootstrap(sb, scratchDir, pathDirs);
        sb.Append(command);
        foreach (var a in argv)
        {
            sb.Append(' ');
            sb.Append(QuoteForPowershell(a));
        }
        var script = sb.ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"{QuoteProcessPath(exe)} -NoProfile -NonInteractive -EncodedCommand {encoded}";
    }

    private static void AppendCmdEnvironmentBootstrap(
        StringBuilder sb,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        AppendCmdSet(sb, "TEMP", scratchDir);
        AppendCmdSet(sb, "TMP", scratchDir);
        AppendCmdSet(sb, "TMPDIR", scratchDir);
        if (pathDirs.Count > 0)
            AppendCmdSet(sb, "PATH", string.Join(Path.PathSeparator, pathDirs));
    }

    private static void AppendCmdSet(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("set \"")
            .Append(name)
            .Append('=')
            .Append(value.Replace("\"", ""))
            .Append("\" && ");
    }

    private static void AppendPowershellEnvironmentBootstrap(
        StringBuilder sb,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        AppendPowershellSet(sb, "TEMP", scratchDir);
        AppendPowershellSet(sb, "TMP", scratchDir);
        AppendPowershellSet(sb, "TMPDIR", scratchDir);
        if (pathDirs.Count > 0)
            AppendPowershellSet(sb, "PATH", string.Join(Path.PathSeparator, pathDirs));
    }

    private static void AppendPowershellSet(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("$env:")
            .Append(name)
            .Append(" = ")
            .Append(QuoteEnvironmentValueForPowershell(value))
            .Append("; ");
    }

    private static string ResolveCmdExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "cmd.exe"
            : Path.Combine(systemRoot, "System32", "cmd.exe");
    }

    private static string ResolveWindowsPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string ResolvePwshExe(IReadOnlyList<string> pathDirs)
    {
        const string executableName = "pwsh.exe";
        foreach (var dir in pathDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Keep PATH resolution best-effort; launch will fail closed if
                // pwsh is not resolvable on the host.
            }
        }

        return executableName;
    }

    private static string QuoteProcessPath(string path)
    {
        if (path.Length > 0 && path.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return path;

        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static string QuoteForCmd(string arg)
    {
        // Note: `%VAR%` env-var expansion inside `cmd /S /C "..."` cannot be
        // reliably suppressed via quoting (cmd parses % before applying quote
        // rules). Bootstrap env refs are rewritten before quoting; callers
        // wanting fully verbatim arguments should use powershell
        // (-EncodedCommand) which has no cmd env-expansion ambiguity.
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%' }) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteForPowershell(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\'', '"', '`', '$' }) < 0)
            return arg;
        return "'" + arg.Replace("'", "''") + "'";
    }

    private static string QuoteEnvironmentValueForPowershell(string value) =>
        "'" + value.Replace("'", "''") + "'";
}

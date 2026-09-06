using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// The mirrored-networking step is the only Local AI setup step allowed to rewrite
/// the user-wide .wslconfig and issue a global <c>wsl.exe --shutdown</c>. Enabling
/// Local AI alone must never authorize that. Without explicit consent the step has
/// to stop before touching anything, so a denied user keeps their exact .wslconfig
/// bytes and their running distributions.
/// </summary>
public sealed class LocalAiWslNetworkingConsentTests
{
    [Fact]
    public async Task Step_WithoutConsent_LeavesWslConfigUntouchedAndIssuesNoShutdown()
    {
        using var temp = new TempDirectory("local-ai-consent-deny-");
        string configPath = Path.Combine(temp.Path, ".wslconfig");
        const string original = "[wsl2]\r\nnetworkingMode=NAT\r\nmemory=8GB\r\n";
        await File.WriteAllTextAsync(configPath, original);
        byte[] before = await File.ReadAllBytesAsync(configPath);

        var manager = new RecordingManager(configPath);
        var commands = new RefusingCommandRunner();
        SetupContext context = CreateContext(temp.Path, consent: false, commands);
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(manager.ApplyCalled);
        Assert.False(manager.RestoreCalled);
        // No process was launched at all, so no global wsl.exe --shutdown was issued.
        Assert.Empty(commands.Invocations);
        Assert.Equal(before, await File.ReadAllBytesAsync(configPath));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        // Denial must not leave a backup or staged copy behind either.
        Assert.Equal([configPath], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Step_EnablingLocalAiAloneDoesNotImplyConsent()
    {
        using var temp = new TempDirectory("local-ai-consent-implicit-");
        string configPath = Path.Combine(temp.Path, ".wslconfig");
        await File.WriteAllTextAsync(configPath, "[wsl2]\r\nnetworkingMode=NAT\r\n");

        var manager = new RecordingManager(configPath);
        var commands = new RefusingCommandRunner();
        // Local AI is enabled, but consent was never granted.
        SetupContext context = CreateContext(temp.Path, consent: false, commands);
        Assert.True(context.Config.LocalAi.Enabled);
        Assert.False(context.Config.LocalAi.WslMirroredNetworkingConsent);

        StepResult result = await new ConfigureLocalAiWslNetworkingStep(_ => manager)
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(manager.ApplyCalled);
        Assert.Empty(commands.Invocations);
    }

    private static SetupContext CreateContext(
        string localDataDirectory,
        bool consent,
        ICommandRunner commands)
    {
        var config = new SetupConfig
        {
            LocalAi = new LocalAiConfig
            {
                Enabled = true,
                WslMirroredNetworkingConsent = consent,
            },
        };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            commands,
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    /// <summary>
    /// Fails the test instead of launching a real process. The denied-consent path
    /// must never reach <c>wsl.exe --shutdown</c>, so any invocation is a defect.
    /// </summary>
    private sealed class RefusingCommandRunner : ICommandRunner
    {
        public List<string> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null,
            bool allowInheritedPipeHandleEscape = false)
        {
            Invocations.Add($"{executable} {string.Join(' ', arguments)}");
            throw new InvalidOperationException(
                $"The step must not run '{executable}' without explicit consent.");
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            Invocations.Add($"wsl:{distroName} {command}");
            throw new InvalidOperationException(
                "The step must not run WSL commands without explicit consent.");
        }
    }

    private sealed class RecordingManager(string configPath) : IWslGlobalConfigManager
    {
        public bool ApplyCalled { get; private set; }
        public bool RestoreCalled { get; private set; }

        public WslGlobalConfigStatus Inspect() => new(File.Exists(configPath), false);

        public WslGlobalConfigApplyResult ApplyMirroredNetworking()
        {
            ApplyCalled = true;
            throw new InvalidOperationException(
                "The step must not apply mirrored networking without explicit consent.");
        }

        public WslGlobalConfigRestoreResult RestoreIfUnchanged()
        {
            RestoreCalled = true;
            return WslGlobalConfigRestoreResult.NoBackup;
        }
    }
}

namespace OpenClaw.SetupEngine.Tests;

public sealed class StartGatewayStepTests
{
    [Fact]
    public async Task ExecuteAsync_StartsAndHealthChecksWithoutPortOwnershipProbe()
    {
        var commands = new RecordingCommandRunner(command =>
        {
            if (command.Contains("ss -tlnp", StringComparison.Ordinal))
                return Ok("LISTEN 0 4096 *:18789 *:*");
            if (command.Contains("openclaw gateway start", StringComparison.Ordinal))
                return Ok();
            if (command.Contains("curl -s -o /dev/null", StringComparison.Ordinal))
                return Ok("200");
            return Ok();
        });
        var config = new SetupConfig
        {
            GatewayPort = 18789,
            Gateway = new GatewayConfig { HealthTimeoutSeconds = 1 }
        };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        var context = new SetupContext(
            config,
            logger,
            journal,
            commands,
            CancellationToken.None)
        {
            DistroName = "test-distro"
        };

        var result = await new StartGatewayStep().ExecuteAsync(
            context,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(
            commands.WslCommands,
            command => command.Contains("ss -tlnp", StringComparison.Ordinal));
        Assert.Contains(
            commands.WslCommands,
            command => command.Contains("openclaw gateway start", StringComparison.Ordinal));
    }

    private static CommandResult Ok(string stdout = "") =>
        new(0, stdout, "", TimeSpan.Zero, TimedOut: false);

    private sealed class RecordingCommandRunner(
        Func<string, CommandResult> runInWsl) : ICommandRunner
    {
        public List<string> WslCommands { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) =>
            Task.FromResult(Ok());

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            WslCommands.Add(command);
            return Task.FromResult(runInWsl(command));
        }
    }
}

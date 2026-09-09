using OpenClaw.Connection;
using OpenClaw.TestSupport;
using OpenClaw.Shared;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localTempDir;
    private readonly string? _prevDataDir;
    private readonly string? _prevLocalDataDir;
    private readonly ITestOutputHelper _output;
    private const string DevicePairPluginNotFoundOutput = "plugins.entries.device-pair: plugin not found: device-pair";
    private const string OtherPluginNotFoundOutput = "plugins.entries.other-plugin: plugin not found: other-plugin";

    public SetupStepsTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"steps-test-{Guid.NewGuid():N}");
        _localTempDir = Path.Combine(Path.GetTempPath(), $"steps-local-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_localTempDir);
        _prevDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        _prevLocalDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _tempDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _localTempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _prevDataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _prevLocalDataDir);
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_localTempDir, recursive: true); } catch { }
    }

    private SetupContext CreateContext(SetupConfig? config = null, ICommandRunner? commands = null)
    {
        var cfg = config ?? new SetupConfig { CleanBeforeRun = true };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        return new SetupContext(cfg, logger, journal, commands ?? new CommandRunner(logger), CancellationToken.None);
    }

    [Fact]
    public async Task PairingEndpointTrust_UnknownLoopbackOwner_BlocksBeforeCredentialUse()
    {
        var context = CreateContext(new SetupConfig
        {
            DistroName = "OpenClawGateway",
            GatewayUrl = "ws://localhost:18789"
        });
        context.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener,
                18789,
                Detail: "unknown owner"));

        var result = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(
            context,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StepOutcome.FailedTerminal, result!.Outcome);
        Assert.Contains("unknown owner", result.Message);
    }

    [Fact]
    public async Task PairingEndpointTrust_TerminalRestartWait_RetriesOnlyNoListener()
    {
        var context = CreateContext(new SetupConfig
        {
            DistroName = "OpenClawGateway",
            GatewayUrl = "ws://localhost:18789"
        });
        var attempts = 0;
        context.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            ++attempts < 3
                ? new GatewayEndpointProvenance(
                    GatewayEndpointProvenanceKind.NoListener,
                    18789)
                : new GatewayEndpointProvenance(
                    GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                    18789));

        var result = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(
            context,
            CancellationToken.None,
            noListenerRetryCount: 2,
            noListenerRetryDelay: TimeSpan.Zero);

        Assert.Null(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task PairingEndpointTrust_TerminalRestartWait_RejectsUnknownOwnerImmediately()
    {
        var context = CreateContext(new SetupConfig
        {
            DistroName = "OpenClawGateway",
            GatewayUrl = "ws://localhost:18789"
        });
        var attempts = 0;
        context.EndpointProvenanceProbe = (_, _) =>
        {
            attempts++;
            return Task.FromResult(new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener,
                18789,
                Detail: "unknown owner"));
        };

        var result = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(
            context,
            CancellationToken.None,
            noListenerRetryCount: 30,
            noListenerRetryDelay: TimeSpan.Zero);

        Assert.NotNull(result);
        Assert.Equal(StepOutcome.FailedTerminal, result!.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void PairingAuthorization_GatesInitialAndReconnectHandshakesForOperatorAndNode()
    {
        var context = CreateContext(new SetupConfig
        {
            DistroName = "OpenClawGateway",
            GatewayUrl = "ws://localhost:18789"
        });
        var operatorIdentityDir = Path.Combine(_tempDir, "operator-identity");
        var nodeIdentityDir = Path.Combine(_tempDir, "node-identity");
        const string gatewayUrl = "ws://localhost:18789";
        using var operatorClient = new OpenClawGatewayClient(
            gatewayUrl,
            "synthetic-operator-token",
            identityPath: operatorIdentityDir);
        using var nodeClient = new WindowsNodeClient(
            gatewayUrl,
            "synthetic-node-token",
            nodeIdentityDir);

        PairOperatorStep.ApplyReconnectAuthorization(operatorClient, context);
        PairOperatorStep.ApplyReconnectAuthorization(nodeClient, context);

        Assert.NotNull(operatorClient.HandshakeAuthorizationAsync);
        Assert.NotNull(operatorClient.ReconnectAuthorizationAsync);
        Assert.NotNull(nodeClient.HandshakeAuthorizationAsync);
        Assert.NotNull(nodeClient.ReconnectAuthorizationAsync);
    }

    [Fact]
    public async Task PairingEndpointTrust_RestartWait_RetriesSnapshotChangeOnly()
    {
        var context = CreateContext(new SetupConfig
        {
            DistroName = "OpenClawGateway",
            GatewayUrl = "ws://localhost:18789"
        });
        var attempts = 0;
        context.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            ++attempts == 1
                ? new GatewayEndpointProvenance(
                    GatewayEndpointProvenanceKind.UnknownListener,
                    18789,
                    FailureReason:
                        GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged)
                : new GatewayEndpointProvenance(
                    GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                    18789));

        var result = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(
            context,
            CancellationToken.None,
            noListenerRetryCount: 1,
            noListenerRetryDelay: TimeSpan.Zero);

        Assert.Null(result);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected, false, 1013, true, null)]
    [InlineData(ConnectionStatus.Disconnected, false, 1013, false, (int)PairOperatorStep.ConnectionOutcome.Error)]
    [InlineData(ConnectionStatus.Disconnected, false, 1012, true, (int)PairOperatorStep.ConnectionOutcome.Error)]
    [InlineData(ConnectionStatus.Disconnected, true, 1013, true, (int)PairOperatorStep.ConnectionOutcome.PairingRequired)]
    [InlineData(ConnectionStatus.Connected, false, null, true, (int)PairOperatorStep.ConnectionOutcome.Connected)]
    [InlineData(ConnectionStatus.Error, false, 1013, true, (int)PairOperatorStep.ConnectionOutcome.Error)]
    public void SetupConnectionStatus_RetriesOnlyStartup1013AfterRestart(
        ConnectionStatus status,
        bool isPairingRequired,
        int? closeStatusCode,
        bool retryGatewayStartupDisconnects,
        int? expected)
    {
        var expectedOutcome = expected is null
            ? null
            : (PairOperatorStep.ConnectionOutcome?)expected.Value;
        Assert.Equal(
            expectedOutcome,
            PairOperatorStep.ClassifySetupConnectionStatus(
                status,
                isPairingRequired,
                closeStatusCode,
                retryGatewayStartupDisconnects));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void DistroInstallPathPolicy_RejectsMissingNames(string? distroName)
    {
        var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            distroName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Equal("WSL distro name is required.", error);
    }

    [Theory]
    [InlineData(@"..\..")]
    [InlineData(@"name\child")]
    [InlineData(@"name/child")]
    [InlineData(@"C:\outside")]
    [InlineData(".")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("name:stream")]
    public void DistroInstallPathPolicy_RejectsUnsafeNames(string distroName)
    {
        var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            distroName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("Invalid WSL distro name", error);
    }

    [Fact]
    public void DistroInstallPathPolicy_ResolvesImmediateChild()
    {
        var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            "OpenClawGateway-Dev",
            out var installPath,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_localTempDir, "wsl", "OpenClawGateway-Dev")),
            installPath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_localTempDir, "wsl")),
            Path.GetDirectoryName(installPath));
    }

    [Fact]
    public void DistroInstallPathPolicy_AcceptsExactly64CharactersForCreation()
    {
        var distroName = $"A{new string('a', 63)}";

        var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            distroName,
            out var installPath,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(Path.Combine(_localTempDir, "wsl", distroName), installPath);
    }

    [Fact]
    public void DistroInstallPathPolicy_Rejects65CharactersForCreation()
    {
        var distroName = $"A{new string('a', 64)}";

        var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            distroName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("1-64", error);
    }

    [Theory]
    [InlineData("OpenClaw Gateway")]
    [InlineData("OpenClaw-网关")]
    public void DistroInstallPathPolicy_AllowsSafeLegacyNamesOnlyForTeardown(string distroName)
    {
        Assert.False(DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            distroName,
            out _,
            out _));

        var resolved = DistroInstallPathPolicy.TryGetManagedInstallPath(
            _localTempDir,
            distroName,
            out var installPath,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(Path.Combine(_localTempDir, "wsl", distroName), installPath);
    }

    [Fact]
    public void DistroInstallPathPolicy_AddsRecoveryOnlyForLegacyTeardownNames()
    {
        const string error = "path validation failed";

        Assert.Contains(
            "--uninstall --confirm-destructive",
            DistroInstallPathPolicy.WithLegacyReplacementGuidance("OpenClaw Gateway", error));
        Assert.Equal(
            error,
            DistroInstallPathPolicy.WithLegacyReplacementGuidance("OpenClawGateway", error));
        Assert.Equal(
            error,
            DistroInstallPathPolicy.WithLegacyReplacementGuidance(@"..\..", error));
    }

    [Theory]
    [InlineData(@"..\..")]
    [InlineData(@"name\child")]
    [InlineData("name/child")]
    [InlineData(@"C:\outside")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("name.")]
    [InlineData("name:stream")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹")]
    [InlineData("LPT².log")]
    [InlineData("CON .txt")]
    [InlineData("LPT1 .log")]
    public void DistroInstallPathPolicy_RejectsUnsafeManagedNames(string distroName)
    {
        var resolved = DistroInstallPathPolicy.TryGetManagedInstallPath(
            _localTempDir,
            distroName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("Invalid managed WSL distro name", error);
    }

    [Fact]
    public async Task SetupPipeline_RejectsLegacyNameBeforeCleanup()
    {
        var legacyPath = Path.Combine(_localTempDir, "wsl", "OpenClaw Gateway");
        Directory.CreateDirectory(legacyPath);
        var sentinel = Path.Combine(legacyPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var commands = new FakeCommandRunner(_ => Ok("OpenClaw Gateway"));
        var ctx = CreateContext(
            new SetupConfig { CleanBeforeRun = true, DistroName = "OpenClaw Gateway" },
            commands);
        var pipeline = new SetupPipeline(
        [
            new ValidateDistroInstallPathStep(),
            new CleanupStaleDistroStep(),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("validate-distro-path", result.FailedStepId);
        Assert.Contains("--uninstall --confirm-destructive", result.Message);
        Assert.True(File.Exists(sentinel));
        Assert.Empty(commands.Calls);
    }

    [Theory]
    [InlineData("OpenClaw Gateway")]
    [InlineData("OpenClaw-网关")]
    public async Task UninstallPipeline_RemovesSafeLegacyDistroBeforeSupportedReplacement(string legacyName)
    {
        var legacyPath = Path.Combine(_localTempDir, "wsl", legacyName);
        Directory.CreateDirectory(legacyPath);
        File.WriteAllText(Path.Combine(legacyPath, "legacy.vhdx"), "legacy");
        var outside = Path.Combine(_localTempDir, "outside");
        Directory.CreateDirectory(outside);
        var outsideSentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(outsideSentinel, "keep");
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok($"{legacyName}\n")
                : Ok(""));
        var ctx = CreateContext(
            new SetupConfig
            {
                ConfirmDestructive = true,
                DistroName = legacyName,
            },
            commands);
        var pipeline = new SetupPipeline([new CreateWslInstanceStep()]);

        var uninstall = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, uninstall.Outcome);
        Assert.False(Directory.Exists(legacyPath));
        Assert.True(File.Exists(outsideSentinel));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments),
            call => Assert.Contains("--terminate", call.Arguments),
            call => Assert.Contains("--unregister", call.Arguments));
        Assert.True(DistroInstallPathPolicy.TryGetNewInstallPath(
            _localTempDir,
            "OpenClawGateway",
            out _,
            out var replacementError),
            replacementError);
    }

    [Fact]
    public async Task CreateWslInstanceRollback_RejectsTraversalBeforeCommandsOrDeletion()
    {
        var outside = Path.Combine(_localTempDir, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var commands = new FakeCommandRunner(_ => Ok(""));
        var ctx = CreateContext(
            new SetupConfig { DistroName = @"..\.." },
            commands);

        var error = await Assert.ThrowsAsync<IOException>(
            () => new CreateWslInstanceStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains("Refusing WSL rollback filesystem cleanup", error.Message);
        Assert.True(File.Exists(sentinel));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task CreateWslInstanceRollback_PreservesVhdWhenUnregisterFails()
    {
        const string legacyName = "OpenClaw Gateway";
        var legacyPath = Path.Combine(_localTempDir, "wsl", legacyName);
        Directory.CreateDirectory(legacyPath);
        var sentinel = Path.Combine(legacyPath, "legacy.vhdx");
        File.WriteAllText(sentinel, "legacy");
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok($"{legacyName}\n");
            if (args.SequenceEqual(["--terminate", legacyName]))
                return Ok();
            if (args.SequenceEqual(["--unregister", legacyName]))
                return Fail("unregister failed");
            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(
            new SetupConfig { DistroName = legacyName },
            commands);

        var error = await Assert.ThrowsAsync<IOException>(
            () => new CreateWslInstanceStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains("Refusing unsafe WSL rollback cleanup", error.Message);
        Assert.True(File.Exists(sentinel));
        Assert.Equal(2, commands.Calls.Count(c => c.Arguments.SequenceEqual(["--unregister", legacyName])));
    }

    [Fact]
    public async Task CreateWslInstanceRollback_RemovesEmptyWslRootWhenDistroIsAlreadyAbsent()
    {
        var wslRoot = Path.Combine(_localTempDir, "wsl");
        Directory.CreateDirectory(wslRoot);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        await new CreateWslInstanceStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.False(Directory.Exists(wslRoot));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Fact]
    public async Task CleanupStaleDistro_RejectsTraversalWithoutDeletingTarget()
    {
        var target = Path.Combine(_localTempDir, "sentinel");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var commands = new FakeCommandRunner(_ => Ok(""));
        var ctx = CreateContext(
            new SetupConfig { CleanBeforeRun = true, DistroName = @"..\.." },
            commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid managed WSL distro name", result.Message);
        Assert.True(File.Exists(sentinel));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task CleanupStaleDistro_PreservesUnownedRegisteredDistro()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(
            new SetupConfig
            {
                CleanBeforeRun = true,
                DistroName = "OpenClawGateway",
            },
            commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("not proven to be managed by OpenClaw", result.Message);
        Assert.Contains("explicitly confirm", result.Message);
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Fact]
    public async Task CleanupStaleDistro_PreservesUnownedOrphanDirectory()
    {
        var distroPath = Path.Combine(_localTempDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(distroPath);
        var sentinel = Path.Combine(distroPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(
            new SetupConfig
            {
                CleanBeforeRun = true,
                DistroName = "OpenClawGateway",
            },
            commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(File.Exists(sentinel));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Fact]
    public void CleanupStaleDistro_AllowsCliDestructiveConfirmation()
    {
        var ctx = CreateContext(new SetupConfig
        {
            ConfirmDestructive = true,
            DistroName = "OpenClawGateway",
        });
        var expectedInstallPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");

        var result = new CleanupStaleDistroStep(
            new FakeWslRegistrationInspector(
                new WslRegistrationInspection(
                    WslRegistrationInspectionStatus.Unavailable)))
            .EnsureRegisteredDistroCleanupAllowed(
            ctx,
            "OpenClawGateway",
            expectedInstallPath);

        Assert.Null(result);
        Assert.Null(CleanupStaleDistroStep.EnsureOrphanDirectoryCleanupAllowed(
            ctx,
            "OpenClawGateway"));
    }

    [Fact]
    public void CleanupStaleDistro_AllowsUiConfirmationOnlyForMatchingDistro()
    {
        var config = new SetupConfig
        {
            ConfirmedDestructiveDistroName = "OpenClawGateway",
            DistroName = "OpenClawGateway",
        };
        var ctx = CreateContext(config);
        var expectedInstallPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        var step = new CleanupStaleDistroStep(
            new FakeWslRegistrationInspector(
                new WslRegistrationInspection(
                    WslRegistrationInspectionStatus.Unavailable)));

        Assert.Null(step.EnsureRegisteredDistroCleanupAllowed(
            ctx,
            "OpenClawGateway",
            expectedInstallPath));

        config.ConfirmedDestructiveDistroName = "OtherGateway";
        var mismatch = step.EnsureRegisteredDistroCleanupAllowed(
            ctx,
            "OpenClawGateway",
            expectedInstallPath);

        Assert.NotNull(mismatch);
        Assert.Equal(StepOutcome.FailedTerminal, mismatch!.Outcome);
    }

    [Fact]
    public void SetupConfig_UiDestructiveConfirmationIsEphemeral()
    {
        var config = new SetupConfig
        {
            ConfirmedDestructiveDistroName = "OpenClawGateway",
        };

        var json = JsonSerializer.Serialize(config);

        Assert.DoesNotContain(nameof(SetupConfig.ConfirmedDestructiveDistroName), json);
    }

    [Fact]
    public void CleanupStaleDistro_HeadlessFailureExplainsCliOverride()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Headless = true,
            DistroName = "OpenClawGateway",
        });
        var expectedInstallPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");

        var result = new CleanupStaleDistroStep(
            new FakeWslRegistrationInspector(
                new WslRegistrationInspection(
                    WslRegistrationInspectionStatus.Unavailable)))
            .EnsureRegisteredDistroCleanupAllowed(
            ctx,
            "OpenClawGateway",
            expectedInstallPath);

        Assert.NotNull(result);
        Assert.Contains("--confirm-destructive", result!.Message);
    }

    [Fact]
    public void CleanupStaleDistro_DoesNotUseMatchingSetupStateForOrphanDirectory()
    {
        File.WriteAllText(
            Path.Combine(_localTempDir, "setup-state.json"),
            """
            {
              "DistroName": "OpenClawGateway",
              "Phase": 13
            }
            """);
        var ctx = CreateContext(new SetupConfig { DistroName = "OpenClawGateway" });

        var result = CleanupStaleDistroStep.EnsureOrphanDirectoryCleanupAllowed(
            ctx,
            "OpenClawGateway");

        Assert.NotNull(result);
        Assert.Equal(StepOutcome.FailedTerminal, result!.Outcome);
    }

    [Fact]
    public void ManagedDistroOwnership_RecognizesExactGatewayRegistryBinding()
    {
        var registry = new GatewayRegistry(_tempDir);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "managed-local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        });
        registry.Save();

        Assert.True(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OtherGateway"));
    }

    [Fact]
    public async Task CleanupStaleDistro_AllowsAutomaticCleanupForExactLiveBasePathBinding()
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            installPath,
            CancellationToken.None);
        var ctx = CreateContext();
        var step = new CleanupStaleDistroStep(
            FakeWslRegistrationInspector.Found(@"\\?\" + installPath));

        var result = step.EnsureRegisteredDistroCleanupAllowed(
            ctx,
            "OpenClawGateway",
            installPath);

        Assert.Null(result);
    }

    [Fact]
    public async Task ManagedDistroOwnership_RequiresExactCanonicalMarkerPath()
    {
        var wrongPath = Path.Combine(_localTempDir, "wsl", "OtherGateway");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            wrongPath,
            CancellationToken.None);

        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task ManagedDistroOwnership_DeletesMarkerOnlyForMatchingDistro()
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OtherGateway");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OtherGateway",
            installPath,
            CancellationToken.None);

        ManagedDistroOwnership.DeleteMarker(
            _localTempDir,
            "OpenClawGateway",
            Path.Combine(_localTempDir, "wsl", "OpenClawGateway"));

        Assert.True(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OtherGateway"));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("setup-state")]
    [InlineData("gateway-registry")]
    public async Task CleanupStaleDistro_PreservesSameNamedReplacementAtDifferentBasePath(
        string evidenceKind)
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        var sentinel = Path.Combine(installPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");

        switch (evidenceKind)
        {
            case "marker":
                await ManagedDistroOwnership.WriteMarkerAsync(
                    _localTempDir,
                    "OpenClawGateway",
                    installPath,
                    CancellationToken.None);
                break;
            case "setup-state":
                File.WriteAllText(
                    Path.Combine(_localTempDir, "setup-state.json"),
                    """{"DistroName":"OpenClawGateway","Phase":13}""");
                break;
            case "gateway-registry":
                var registry = new GatewayRegistry(_tempDir);
                registry.AddOrUpdate(new GatewayRecord
                {
                    Id = "stale-managed-local",
                    Url = "ws://localhost:18789",
                    IsLocal = true,
                    SetupManagedDistroName = "OpenClawGateway",
                });
                registry.Save();
                break;
            default:
                throw new InvalidOperationException($"Unknown evidence kind: {evidenceKind}");
        }

        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected destructive call: {string.Join(' ', args)}"));
        var replacementBasePath = Path.Combine(
            _localTempDir,
            "foreign",
            "OpenClawGateway");
        var step = new CleanupStaleDistroStep(
            FakeWslRegistrationInspector.Found(replacementBasePath));
        var ctx = CreateContext(commands: commands);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(File.Exists(sentinel));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Theory]
    [InlineData("setup-state")]
    [InlineData("gateway-registry")]
    public async Task CleanupStaleDistro_NameOnlyEvidenceDoesNotDeleteOrphanDirectory(
        string evidenceKind)
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        var sentinel = Path.Combine(installPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");

        if (evidenceKind == "setup-state")
        {
            File.WriteAllText(
                Path.Combine(_localTempDir, "setup-state.json"),
                """{"DistroName":"OpenClawGateway","Phase":13}""");
        }
        else
        {
            var registry = new GatewayRegistry(_tempDir);
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = "stale-managed-local",
                Url = "ws://localhost:18789",
                IsLocal = true,
                SetupManagedDistroName = "OpenClawGateway",
            });
            registry.Save();
        }

        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected destructive call: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(File.Exists(sentinel));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Theory]
    [InlineData(nameof(WslRegistrationInspectionStatus.NotFound))]
    [InlineData(nameof(WslRegistrationInspectionStatus.Unavailable))]
    [InlineData(nameof(WslRegistrationInspectionStatus.Duplicate))]
    [InlineData(nameof(WslRegistrationInspectionStatus.Malformed))]
    public async Task CleanupStaleDistro_UnknownOrMalformedRegistrationFailsClosed(
        string statusName)
    {
        var status = Enum.Parse<WslRegistrationInspectionStatus>(statusName);
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        var sentinel = Path.Combine(installPath, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            installPath,
            CancellationToken.None);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected destructive call: {string.Join(' ', args)}"));
        var step = new CleanupStaleDistroStep(
            new FakeWslRegistrationInspector(
                new WslRegistrationInspection(status)));
        var ctx = CreateContext(commands: commands);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(File.Exists(sentinel));
        Assert.Collection(
            commands.Calls,
            call => Assert.Equal(["--list", "--quiet"], call.Arguments));
    }

    [Fact]
    public void WslRegistrationInspector_AcceptsEquivalentExtendedDriveBasePath()
    {
        var expectedPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        var inspector = new WindowsWslRegistrationInspector(
            new FakeWslRegistrationSource(
                new WslRegistrationSnapshot(
                    true,
                    [
                        new RawWslRegistration(
                            Guid.NewGuid().ToString("B"),
                            "OpenClawGateway",
                            @"\\?\" + expectedPath),
                    ])));

        var inspection = inspector.Inspect("OpenClawGateway");

        Assert.Equal(WslRegistrationInspectionStatus.Found, inspection.Status);
        Assert.True(DistroInstallPathPolicy.PathsReferToSameLocation(
            expectedPath,
            inspection.BasePath!));
    }

    [Fact]
    public void WslRegistrationInspector_RejectsDuplicateAndMalformedMetadata()
    {
        var expectedPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        var duplicate = new WindowsWslRegistrationInspector(
            new FakeWslRegistrationSource(
                new WslRegistrationSnapshot(
                    true,
                    [
                        new RawWslRegistration(
                            Guid.NewGuid().ToString("B"),
                            "OpenClawGateway",
                            expectedPath),
                        new RawWslRegistration(
                            Guid.NewGuid().ToString("B"),
                            "openclawgateway",
                            expectedPath),
                    ])));
        var malformed = new WindowsWslRegistrationInspector(
            new FakeWslRegistrationSource(
                new WslRegistrationSnapshot(
                    true,
                    [
                        new RawWslRegistration(
                            Guid.NewGuid().ToString("B"),
                            "OpenClawGateway",
                            @"relative\path"),
                    ])));
        var incomplete = new WindowsWslRegistrationInspector(
            new FakeWslRegistrationSource(
                new WslRegistrationSnapshot(
                    false,
                    [],
                    "synthetic read failure")));

        Assert.Equal(
            WslRegistrationInspectionStatus.Duplicate,
            duplicate.Inspect("OpenClawGateway").Status);
        Assert.Equal(
            WslRegistrationInspectionStatus.Malformed,
            malformed.Inspect("OpenClawGateway").Status);
        Assert.Equal(
            WslRegistrationInspectionStatus.Unavailable,
            incomplete.Inspect("OpenClawGateway").Status);
    }

    [Fact]
    public async Task ManagedDistroOwnership_DoesNotDeleteMarkerWithMismatchedPath()
    {
        var expectedPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        var wrongPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OtherGateway");
        var markerPath = Path.Combine(
            _localTempDir,
            "setup-managed-distro.json");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            wrongPath,
            CancellationToken.None);

        var deleted = ManagedDistroOwnership.DeleteMarker(
            _localTempDir,
            "OpenClawGateway",
            expectedPath);

        Assert.False(deleted);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task ManagedDistroOwnership_MarkerDeleteIoRaceIsHandled()
    {
        var expectedPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        var markerPath = Path.Combine(
            _localTempDir,
            "setup-managed-distro.json");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            expectedPath,
            CancellationToken.None);
        using var markerLock = File.Open(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var deleted = ManagedDistroOwnership.DeleteMarker(
            _localTempDir,
            "OpenClawGateway",
            expectedPath);

        Assert.False(deleted);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task CleanupStaleDistro_DeletesOwnedOrphanAndScopedMarker()
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "stale");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            installPath,
            CancellationToken.None);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(Directory.Exists(installPath));
        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task CleanupStaleDistro_DeletesMarkerAfterConfirmedAbsence()
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            installPath,
            CancellationToken.None);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task CleanupStaleDistro_PreservesMarkerWhenInventoryIsUnknown()
    {
        var installPath = Path.Combine(
            _localTempDir,
            "wsl",
            "OpenClawGateway");
        await ManagedDistroOwnership.WriteMarkerAsync(
            _localTempDir,
            "OpenClawGateway",
            installPath,
            CancellationToken.None);
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Fail("synthetic inventory failure")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CleanupStaleDistroStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task CreateWslInstance_ClaimsOwnershipBeforeInstallStarts()
    {
        var markerExistedDuringInstall = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok("");
            if (args.Contains("--install"))
            {
                markerExistedDuringInstall = ManagedDistroOwnership.HasEvidence(
                    _tempDir,
                    _localTempDir,
                    "OpenClawGateway");
                return Fail("synthetic install failure");
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(
            new SetupConfig { DistroName = "OpenClawGateway" },
            commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.True(markerExistedDuringInstall);
        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public void ManagedDistroOwnership_RejectsMismatchedSetupState()
    {
        File.WriteAllText(
            Path.Combine(_localTempDir, "setup-state.json"),
            """
            {
              "DistroName": "DifferentGateway",
              "Phase": 13
            }
            """);

        Assert.False(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task DeleteDistroDirectory_RejectsPathOutsideExpectedImmediateChild()
    {
        var target = Path.Combine(_localTempDir, "sentinel");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var ctx = CreateContext();

        var result = await CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(
            ctx,
            "OpenClawGateway",
            target,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Refusing to delete WSL path", result.Message);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task DeleteDistroDirectory_RevalidatesAncestorsBeforeRetry()
    {
        var wslRoot = Path.Combine(_localTempDir, "wsl");
        var target = Path.Combine(wslRoot, "OpenClawGateway");
        var lockedPath = Path.Combine(target, "locked.txt");
        Directory.CreateDirectory(target);
        File.WriteAllText(lockedPath, "locked");

        var outsideRoot = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var redirectedTarget = Path.Combine(outsideRoot, "OpenClawGateway");
        Directory.CreateDirectory(redirectedTarget);
        var sentinel = Path.Combine(redirectedTarget, "keep.txt");
        File.WriteAllText(sentinel, "keep");

        var ctx = CreateContext();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Logger.LogEmitted += (_, entry) =>
        {
            if (entry.Message.Contains("retrying", StringComparison.Ordinal))
                retryStarted.TrySetResult();
        };

        try
        {
            using var lockedFile = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var deleteTask = CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(
                ctx,
                "OpenClawGateway",
                target,
                CancellationToken.None);

            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lockedFile.Dispose();
            Directory.Delete(wslRoot, recursive: true);
            CreateJunction(wslRoot, outsideRoot);

            var result = await deleteTask;

            Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
            Assert.Contains("reparse point", result.Message);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(wslRoot); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(outsideRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CreateWslInstance_RejectsTraversalBeforeRunningWsl()
    {
        var commands = new FakeCommandRunner(_ => Ok(""));
        var ctx = CreateContext(
            new SetupConfig { DistroName = @"..\.." },
            commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid WSL distro name", result.Message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsWhenWslRootIsJunction()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var sentinel = Path.Combine(outsideDir, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var wslRoot = Path.Combine(_localTempDir, "wsl");

        try
        {
            CreateJunction(wslRoot, outsideDir);

            var candidate = Path.Combine(wslRoot, "OpenClawGateway");
            var allowed = DistroInstallPathPolicy.TryValidateDeleteTarget(
                _localTempDir,
                "OpenClawGateway",
                candidate,
                out _,
                out var error);

            Assert.False(allowed);
            Assert.Contains("reparse point", error);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(wslRoot); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsWhenManagedChildIsJunction()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var sentinel = Path.Combine(outsideDir, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var wslRoot = Path.Combine(_localTempDir, "wsl");
        Directory.CreateDirectory(wslRoot);
        var managedChild = Path.Combine(wslRoot, "OpenClaw Gateway");

        try
        {
            CreateJunction(managedChild, outsideDir);

            var resolved = DistroInstallPathPolicy.TryGetManagedInstallPath(
                _localTempDir,
                "OpenClaw Gateway",
                out _,
                out var error);

            Assert.False(resolved);
            Assert.Contains("reparse point", error);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(managedChild); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsUnicodeNormalizationCollision()
    {
        var wslRoot = Path.Combine(_localTempDir, "wsl");
        Directory.CreateDirectory(Path.Combine(wslRoot, "OpenClaw-é"));
        Directory.CreateDirectory(Path.Combine(wslRoot, "OpenClaw-e\u0301"));

        var resolved = DistroInstallPathPolicy.TryGetManagedInstallPath(
            _localTempDir,
            "OpenClaw-é",
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("ambiguous", error);
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsCaseCollision()
    {
        var wslRoot = Path.Combine(_localTempDir, "wsl");
        Directory.CreateDirectory(wslRoot);
        EnableCaseSensitiveDirectory(wslRoot);
        Directory.CreateDirectory(Path.Combine(wslRoot, "OpenClaw Gateway"));
        Directory.CreateDirectory(Path.Combine(wslRoot, "openclaw gateway"));

        var resolved = DistroInstallPathPolicy.TryGetManagedInstallPath(
            _localTempDir,
            "OpenClaw Gateway",
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("ambiguous", error);
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsJunctionAncestorForInstall()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var wslRoot = Path.Combine(_localTempDir, "wsl");

        try
        {
            CreateJunction(wslRoot, outsideDir);

            var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
                _localTempDir,
                "OpenClawGateway",
                out _,
                out var error);

            Assert.False(resolved);
            Assert.Contains("reparse point", error);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(wslRoot); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DistroInstallPathPolicy_RejectsJunctionAtLocalDataDirWithTrailingSeparator()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var lddJunction = Path.Combine(Path.GetTempPath(), $"ldd-{Guid.NewGuid():N}");

        try
        {
            CreateJunction(lddJunction, outsideDir);

            var resolved = DistroInstallPathPolicy.TryGetNewInstallPath(
                lddJunction + Path.DirectorySeparatorChar,
                "OpenClawGateway",
                out _,
                out var error);

            Assert.False(resolved);
            Assert.Contains("reparse point", error);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(lddJunction); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Junction (mklink /J) does not require elevation, unlike symbolic links.
        using var mklink = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start mklink.");

        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static void EnableCaseSensitiveDirectory(string path)
    {
        using var fsutil = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "fsutil.exe",
                ArgumentList = { "file", "SetCaseSensitiveInfo", path, "enable" },
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start fsutil.");

        fsutil.WaitForExit();
        Assert.Equal(0, fsutil.ExitCode);
    }

    [Fact]
    public void WriteSettingsJson_AppliesConfiguredCapabilitiesBeforePersisting()
    {
        var config = new SetupConfig
        {
            Capabilities = new CapabilitiesConfig
            {
                System = false,
                Canvas = true,
                Screen = true,
                Camera = false,
                Location = false,
                Browser = false,
                Device = true,
                Tts = true,
                Stt = false,
            },
        };
        var ctx = CreateContext(config);

        VerifyEndToEndStep.WriteSettingsJson(ctx);

        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "settings.json")));
        Assert.False(result.RootElement.GetProperty("NodeSystemRunEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeCanvasEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeScreenEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeLocationEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeBrowserProxyEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeTtsEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeSttEnabled").GetBoolean());
    }

    // ─── CleanupStaleGatewayStep: Preserve non-local records ───

    [Fact]
    public async Task CleanupStaleGateway_RemovesLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Null(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesSshTunneledRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a gateway record with SSH tunnel (remote gateway using localhost)
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "tunneled-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = new SshTunnelConfig("user", "remote.host", 18789, 18789),
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesNonLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a non-local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "remote-gw",
            Url = gatewayUrl,
            IsLocal = false,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_DeletesIdentityDirectoryForLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw-with-identity",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
        });
        registry.SetActive("local-gw-with-identity");
        registry.Save();

        // Create an identity directory
        var identityDir = registry.GetIdentityDirectory("local-gw-with-identity");
        Directory.CreateDirectory(identityDir);
        File.WriteAllText(Path.Combine(identityDir, "device-key.json"), "{}");

        var step = new CleanupStaleGatewayStep();
        await step.ExecuteAsync(ctx, CancellationToken.None);

        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Null(reloaded.GetById("local-gw-with-identity"));
        Assert.Null(reloaded.ActiveGatewayId);
        Assert.False(Directory.Exists(identityDir));
    }

    [Fact]
    public async Task CleanupStaleGateway_MixedRecords_RemovesAllManagedDuplicatesAndPreservesProtectedRecords()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();

        var preservedRecords = new[]
        {
            new GatewayRecord
            {
                Id = "00-unmanaged-active",
                Url = gatewayUrl,
                FriendlyName = "Manual localhost",
                IsLocal = true,
            },
            new GatewayRecord
            {
                Id = "01-other-distro",
                Url = "ws://127.0.0.1:18789",
                IsLocal = true,
                SetupManagedDistroName = "ProtectedGateway",
            },
            new GatewayRecord
            {
                Id = "02-remote",
                Url = gatewayUrl,
                IsLocal = false,
                SetupManagedDistroName = ctx.DistroName,
            },
            new GatewayRecord
            {
                Id = "03-ssh-tunneled",
                Url = gatewayUrl,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
                SshTunnel = new SshTunnelConfig("user", "remote.host", 18789, 18789),
            },
            new GatewayRecord
            {
                Id = "04-non-equivalent",
                Url = "ws://localhost:18790",
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
            },
        };
        var staleRecords = new[]
        {
            new GatewayRecord
            {
                Id = "10-managed-localhost",
                Url = gatewayUrl,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
            },
            new GatewayRecord
            {
                Id = "20-managed-loopback-alias",
                Url = "http://127.0.0.1:18789",
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
            },
        };

        foreach (var record in preservedRecords.Concat(staleRecords))
        {
            registry.AddOrUpdate(record);
            var identityDir = registry.GetIdentityDirectory(record.Id);
            Directory.CreateDirectory(identityDir);
            File.WriteAllText(Path.Combine(identityDir, "identity.marker"), record.Id);
        }
        registry.SetActive(preservedRecords[0].Id);
        registry.Save();

        var result = await new CleanupStaleGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Equal(
            preservedRecords.Select(record => record.Id),
            reloaded.GetAll().Select(record => record.Id));
        Assert.Equal(preservedRecords[0].Id, reloaded.ActiveGatewayId);
        foreach (var record in preservedRecords)
        {
            Assert.Equal(
                record.Id,
                File.ReadAllText(Path.Combine(
                    reloaded.GetIdentityDirectory(record.Id),
                    "identity.marker")));
        }
        foreach (var record in staleRecords)
            Assert.False(Directory.Exists(reloaded.GetIdentityDirectory(record.Id)));
    }

    [Fact]
    public async Task CleanupStaleGateway_IdentityDeleteFailure_RestoresOnlyFailedRecordForRetry()
    {
        var ctx = CreateContext();
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        foreach (var id in new[] { "blocked-managed", "clean-managed" })
        {
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = id,
                Url = ctx.GatewayUrl!,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
            });
            var identityDir = registry.GetIdentityDirectory(id);
            Directory.CreateDirectory(identityDir);
            File.WriteAllText(Path.Combine(identityDir, "identity.marker"), id);
        }
        registry.SetActive("blocked-managed");
        registry.Save();

        var blockedIdentityPath = Path.Combine(
            registry.GetIdentityDirectory("blocked-managed"),
            "identity.marker");
        await using (File.Open(
            blockedIdentityPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            var error = await Assert.ThrowsAsync<AggregateException>(
                () => new CleanupStaleGatewayStep().ExecuteAsync(
                    ctx,
                    CancellationToken.None));

            Assert.Contains(
                "registry records were restored",
                error.Message,
                StringComparison.Ordinal);
            var afterFailure = new GatewayRegistry(_tempDir);
            afterFailure.Load();
            Assert.NotNull(afterFailure.GetById("blocked-managed"));
            Assert.Null(afterFailure.GetById("clean-managed"));
            Assert.Equal("blocked-managed", afterFailure.ActiveGatewayId);
            Assert.True(Directory.Exists(
                afterFailure.GetIdentityDirectory("blocked-managed")));
            Assert.False(Directory.Exists(
                afterFailure.GetIdentityDirectory("clean-managed")));
        }

        var retry = await new CleanupStaleGatewayStep().ExecuteAsync(
            ctx,
            CancellationToken.None);

        Assert.True(retry.IsSuccess);
        var afterRetry = new GatewayRegistry(_tempDir);
        afterRetry.Load();
        Assert.Empty(afterRetry.GetAll());
        Assert.Null(afterRetry.ActiveGatewayId);
        Assert.False(Directory.Exists(
            afterRetry.GetIdentityDirectory("blocked-managed")));
    }

    [Fact]
    public async Task CleanupStaleGateway_SkippedWhenCleanBeforeRunFalse()
    {
        var ctx = CreateContext(new SetupConfig { CleanBeforeRun = false });

        var step = new CleanupStaleGatewayStep();
        Assert.True(step.CanSkip(ctx));
    }

    // ─── InstallCliStep: URL validation and quoting ───

    [Fact]
    public async Task PreflightPort_Loopback_SucceedsForAvailablePort()
    {
        var port = GetFreeTcpPort();
        var ctx = CreateContext(new SetupConfig { GatewayPort = port });

        var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task PreflightPort_Lan_FailsWhenLoopbackPortInUse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0)
        {
            ExclusiveAddressUse = true
        };
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ctx = CreateContext(new SetupConfig
            {
                GatewayPort = port,
                Gateway = new GatewayConfig { Bind = "lan" }
            });

            var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("already in use", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WaitForPortFree_ReturnsImmediately_WhenPortIsAlreadyFree()
    {
        var port = GetFreeTcpPort();
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);

        // Should complete well within 1 second because the port is already free
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PreflightPortStep.WaitForPortFreeAsync(port, "loopback", logger, cts.Token, maxWaitSeconds: 10);
        // No assertion needed — completing without cancellation/timeout is the success condition
    }

    [Fact]
    public async Task WaitForPortFree_PollsUntilPortReleased()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);

        // Release the port after a short delay (simulates WSL proxy teardown lag)
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            listener.Stop();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await PreflightPortStep.WaitForPortFreeAsync(port, "loopback", logger, cts.Token, maxWaitSeconds: 5);

        // Port should now be free
        Assert.True(PreflightPortStep.CanBind(IPAddress.Loopback, port, out _));
    }

    [Fact]
    public async Task PreflightPort_Loopback_SucceedsAfterPortReleasedDuringPoll()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Release after 300ms — simulates a slow WSL proxy shutdown
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            listener.Stop();
        });

        var ctx = CreateContext(new SetupConfig { GatewayPort = port });
        var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InstallCli_RejectsHttpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "http://evil.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_RejectsMissingExactVersion()
    {
        var error = Assert.Throws<ArgumentException>(
            () => InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli.sh", null));

        Assert.Contains("exact version", error.Message);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_DownloadsCompletelyBeforeExecuting()
    {
        var command = InstallCliStep.BuildInstallCommand(
            "https://openclaw.ai/install-cli.sh",
            "2026.5.22",
            GatewayReleasePolicy.NodeVersion);

        Assert.StartsWith("set -euo pipefail", command);
        Assert.Contains("umask 077", command);
        Assert.Contains("installer_dir='/tmp/openclaw-installer-", command);
        Assert.Contains("mkdir -m 0700 -- \"$installer_dir\"", command);
        Assert.Contains("installer=\"$installer_dir/installer.sh\"", command);
        Assert.Contains("trap 'rm -rf -- \"$installer_dir\"' EXIT", command);
        Assert.Contains("--connect-timeout 15", command);
        Assert.Contains("--max-time 60", command);
        Assert.DoesNotContain("--remove-on-error", command);
        Assert.Contains("--proto '=https'", command);
        Assert.Contains("--tlsv1.2", command);
        Assert.Contains("--output \"$installer\"", command);
        Assert.Contains("'https://openclaw.ai/install-cli.sh'", command);
        Assert.Contains("if ! test -s \"$installer\"", command);
        Assert.Contains("bash -s -- --version '2026.5.22' --node-version '24.19.0' < \"$installer\"", command);
        Assert.DoesNotContain("--retry", command);
        Assert.DoesNotContain("| bash", command);
    }

    [Fact]
    public void InstallCli_RetryPolicyLimitsPipelineToTwoTransfers()
    {
        var step = new InstallCliStep();
        var command = InstallCliStep.BuildInstallCommand(
            "https://openclaw.ai/install-cli.sh",
            GatewayReleasePolicy.RecommendedVersion,
            GatewayReleasePolicy.NodeVersion);

        Assert.Equal(2, step.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), step.Retry.EffectiveInitialDelay);
        Assert.Equal(InstallCliStep.DownloadMaxTimeSeconds, 60);
        Assert.DoesNotContain("--retry", command);
    }

    [Fact]
    public void InstallCli_BuildInstallCommandPreviewUsesProductionInvocationShape()
    {
        const string installerDirectory =
            "/tmp/openclaw-installer-0123456789abcdef0123456789abcdef";
        var production = InstallCliStep.BuildInstallCommand(
            "https://openclaw.ai/install-cli.sh",
            "2026.5.22",
            GatewayReleasePolicy.NodeVersion,
            installerDirectory);

        var preview = InstallCliStep.BuildInstallCommandPreview(
            "https://openclaw.ai/install-cli.sh",
            "2026.5.22",
            GatewayReleasePolicy.NodeVersion);

        Assert.Equal(
            production.Replace(
                installerDirectory,
                InstallCliStep.InstallerTempDirectoryPreview,
                StringComparison.Ordinal),
            preview);
    }

    [Fact]
    public void InstallCli_BuildInstallCommandPreviewDoesNotRewriteCustomInstallerUrl()
    {
        const string installUrl =
            "https://example.test/00000000000000000000000000000000/install.sh";

        var preview = InstallCliStep.BuildInstallCommandPreview(
            installUrl,
            "2026.5.22");

        Assert.Contains($"'{installUrl}'", preview);
        Assert.Contains(
            $"installer_dir='{InstallCliStep.InstallerTempDirectoryPreview}'",
            preview);
        Assert.DoesNotContain("--connect-timeout", preview);
        Assert.DoesNotContain("--max-time", preview);
        Assert.DoesNotContain("--remove-on-error", preview);
    }

    [Fact]
    public async Task InstallCli_InstallFailureSurfacesStdoutWhenStderrIsEmpty()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? FailWithStdout("ERROR: Node 22.22.3 is unsupported; use Node 24.16.0+.")
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Node 22.22.3 is unsupported", result.Message);
        AssertCleanupRan(commands);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_EscapesSingleQuotesInUrlAndVersion()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli's.sh", "2026.5.22'a");

        Assert.Contains("'https://openclaw.ai/install-cli'\\''s.sh'", command);
        Assert.Contains("--version '2026.5.22'\\''a'", command);
    }

    [Fact]
    public async Task InstallCli_DownloadFailurePreservesCurlError()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(6, "", "curl: (6) Could not resolve host: openclaw.ai", TimeSpan.Zero, TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exit 6", result.Message);
        Assert.Contains("Could not resolve host: openclaw.ai", result.Message);
        Assert.True(commands.WslCalls[0].InputViaStdin);
        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_TransientDnsFailureRecoversOnSecondPipelineAttempt()
    {
        var downloadAttempts = 0;
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command.Contains("curl -fsSL", StringComparison.Ordinal))
                {
                    downloadAttempts++;
                    return downloadAttempts == 1
                        ? new CommandResult(
                            6,
                            "",
                            "curl: (6) Could not resolve host: openclaw.ai",
                            TimeSpan.Zero,
                            TimedOut: false)
                        : Ok();
                }

                if (command.Contains("tools/node/bin/node --version", StringComparison.Ordinal))
                    return Ok($"v{GatewayReleasePolicy.NodeVersion}");
                if (command.EndsWith("openclaw --version", StringComparison.Ordinal))
                    return Ok($"OpenClaw {GatewayReleasePolicy.RecommendedVersion}");
                if (command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal))
                    return Ok();
                return Ok();
            });
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);
        var step = new InstallCliStep();

        var result = await RetryExecutor.ExecuteWithRetry(
            () => step.ExecuteAsync(ctx, CancellationToken.None),
            step.Retry with { InitialDelay = TimeSpan.FromMilliseconds(1) },
            ctx.Logger,
            step.Id,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(step.Retry.MaxAttempts, downloadAttempts);
        Assert.Equal(
            downloadAttempts,
            commands.WslCalls.Count(call => call.Command.Contains("curl -fsSL", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task InstallCli_TransferTimeoutPreservesCurlExitAndStderr()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    28,
                    "",
                    "curl: (28) Operation timed out after 60000 milliseconds",
                    TimeSpan.FromSeconds(60),
                    TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exit 28", result.Message);
        Assert.Contains("Operation timed out after 60000 milliseconds", result.Message);
        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_CommandTimeoutReportsDeadlineInsteadOfSyntheticExit()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    -1,
                    "",
                    "last installer diagnostic",
                    InstallCliStep.InstallerCommandTimeout,
                    TimedOut: true)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("timed out after 5 minutes", result.Message);
        Assert.Contains("last installer diagnostic", result.Message);
        Assert.DoesNotContain("exit -1", result.Message);
        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_PartialTransferDoesNotContinueToVerification()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    18,
                    "",
                    "curl: (18) transfer closed with outstanding read data remaining",
                    TimeSpan.Zero,
                    TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exit 18", result.Message);
        Assert.Contains("outstanding read data", result.Message);
        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_EmptyDownloadDoesNotContinueToVerification()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    65,
                    "",
                    "CLI installer download was empty.",
                    TimeSpan.Zero,
                    TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download was empty", result.Message);
        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_CallerCancellationStillRunsIndependentCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command.Contains("curl -fsSL", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                return command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? Ok()
                    : throw new InvalidOperationException($"Unexpected command: {command}");
            });
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new InstallCliStep().ExecuteAsync(ctx, cancellation.Token));

        var cleanup = AssertCleanupRan(commands);
        Assert.NotEqual(cancellation.Token, cleanup.CancellationToken);
        Assert.False(cleanup.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InstallCli_CleanupExceptionDoesNotMaskDownloadFailure()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    6,
                    "",
                    "curl: (6) Could not resolve host: openclaw.ai",
                    TimeSpan.Zero,
                    TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? throw new IOException("cleanup launch failed")
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        StepResult result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exit 6", result.Message);
        Assert.Contains("Could not resolve host: openclaw.ai", result.Message);
        Assert.Contains("Cleanup also failed: failed (IOException)", result.Message);
    }

    [Fact]
    public async Task InstallCli_CleanupExceptionDoesNotMaskCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command.Contains("curl -fsSL", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                return command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? throw new IOException("cleanup launch failed")
                    : throw new InvalidOperationException($"Unexpected command: {command}");
            });
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new InstallCliStep().ExecuteAsync(ctx, cancellation.Token));

        AssertCleanupRan(commands);
    }

    [Fact]
    public async Task InstallCli_CleanupCancellationDoesNotMaskDownloadFailure()
    {
        using var cleanupCancellation = new CancellationTokenSource();
        cleanupCancellation.Cancel();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("curl -fsSL", StringComparison.Ordinal)
                ? new CommandResult(
                    6,
                    "",
                    "curl: (6) Could not resolve host: openclaw.ai",
                    TimeSpan.Zero,
                    TimedOut: false)
                : command.StartsWith("rm -rf -- /tmp/openclaw-installer-", StringComparison.Ordinal)
                    ? throw new OperationCanceledException(cleanupCancellation.Token)
                    : throw new InvalidOperationException($"Unexpected command: {command}"));
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        StepResult result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exit 6", result.Message);
        Assert.Contains("Could not resolve host: openclaw.ai", result.Message);
        Assert.Contains("Cleanup also failed: was cancelled", result.Message);
    }

    private static (
        string DistroName,
        string Command,
        TimeSpan Timeout,
        string? User,
        bool InputViaStdin,
        CancellationToken CancellationToken) AssertCleanupRan(FakeCommandRunner commands)
    {
        Assert.Equal(2, commands.WslCalls.Count);
        var cleanup = commands.WslCalls[1];
        Assert.StartsWith("rm -rf -- /tmp/openclaw-installer-", cleanup.Command);
        Assert.Equal(TimeSpan.FromSeconds(15), cleanup.Timeout);
        Assert.False(cleanup.InputViaStdin);
        return cleanup;
    }

    [Fact]
    public async Task InstallCli_CandidatePackageCancellationDuringCopy_CleansStagingDirectory()
    {
        var packagePath = Path.Combine(_tempDir, "openclaw-current.tgz");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok(),
            (_, _, _, ct) =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Ok();
            });
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "2026.8.1",
                ValidationPackagePath = packagePath
            }
        };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new InstallCliStep().ExecuteAsync(ctx, cancellation.Token));

        var cleanup = Assert.Single(
            commands.WslCalls,
            call => call.Command == "rm -rf -- /var/lib/openclaw/setup-package");
        Assert.NotEqual(cancellation.Token, cleanup.CancellationToken);
        Assert.False(cleanup.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InstallCli_InstalledVersionMismatchFailsTerminally()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("--version", StringComparison.Ordinal)
                ? Ok("OpenClaw 2026.7.1-2")
                : Ok());
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        var error = Assert.IsType<GatewayCompatibilityException>(result.Error);
        Assert.Equal(GatewayCompatibilityFailureKind.InstalledVersionMismatch, error.Kind);
    }

    [Fact]
    public async Task InstallCli_InstalledRuntimeMismatchFailsTerminally()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command.StartsWith("curl ", StringComparison.Ordinal))
                    return Ok();
                if (command.Contains("tools/node/bin/node --version", StringComparison.Ordinal))
                    return Ok("v24.15.0");
                if (command.EndsWith("openclaw --version", StringComparison.Ordinal))
                    return Ok($"OpenClaw {GatewayReleasePolicy.RecommendedVersion}");
                return Ok();
            });
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);
        var ctx = CreateContext(config, commands);

        var result = await new InstallCliStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        var error = Assert.IsType<GatewayCompatibilityException>(result.Error);
        Assert.Equal(GatewayCompatibilityFailureKind.InstalledRuntimeMismatch, error.Kind);
    }

    [Fact]
    public async Task PreflightWsl_FailsForUnsupportedDirectInstallVersion()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? Ok("WSL version: 2.3.0.0\n")
                : Ok());
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Update WSL", result.Message);
        Assert.Contains(WslInstallSupport.UpdateUrl, result.Message);
    }

    [Fact]
    public async Task PreflightWsl_FailsWithUpdateMessageWhenVersionCommandIsUnsupported()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? new CommandResult(1, "", "Invalid command line option: --version", TimeSpan.Zero, TimedOut: false)
                : Ok());
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("too old", result.Message);
        Assert.Contains(WslInstallSupport.UpdateUrl, result.Message);
    }

    [Fact]
    public async Task PreflightWsl_MissingPlatformIsInstallableWithoutMutation()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? new CommandResult(
                    1,
                    "",
                    "Windows Subsystem for Linux is not installed. See https://aka.ms/wslinstall",
                    TimeSpan.Zero,
                    TimedOut: false)
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(WslViabilityKind.Installable, ctx.WslViability?.Kind);
        Assert.Contains("before downloading Local AI", result.Message);
        Assert.DoesNotContain(commands.Calls, call => call.Arguments.Contains("--install"));
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task WslPipeline_ReusesPreflightResultBeforeInstallingMissingPlatform()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] when !installed => new CommandResult(
                1,
                "",
                "Windows Subsystem for Linux is not installed. See https://aka.ms/wslinstall",
                TimeSpan.Zero,
                TimedOut: false),
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var ensure = new EnsureWslPlatformStep(
            (_, _) =>
            {
                installed = true;
                return Task.FromResult(StepResult.Ok("installed"));
            },
            reusePreflightResult: true);
        var pipeline = new SetupPipeline([new PreflightWslStep(), ensure]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(2, commands.Calls.Count(call => call.Arguments is ["--version"]));
        Assert.Single(commands.Calls, call => call.Arguments is ["--status"]);
    }

    [Fact]
    public async Task WslPipeline_ReusesReadyPreflightResultWithoutReinspection()
    {
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var ensure = new EnsureWslPlatformStep(
            (_, _) =>
            {
                installCalls++;
                return Task.FromResult(StepResult.Ok("installed"));
            },
            reusePreflightResult: true);
        var pipeline = new SetupPipeline([new PreflightWslStep(), ensure]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(0, installCalls);
        Assert.Single(commands.Calls, call => call.Arguments is ["--version"]);
        Assert.Single(commands.Calls, call => call.Arguments is ["--status"]);
    }

    [Fact]
    public async Task WslPipeline_BoundsHungVersionInspectionToOneProbe()
    {
        var commands = new FakeCommandRunner(args => args is ["--version"]
            ? new CommandResult(-1, "", "", TimeSpan.FromSeconds(5), TimedOut: true)
            : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);
        var pipeline = new SetupPipeline(
            [
                new PreflightWslStep(),
                new EnsureWslPlatformStep(
                    (_, _) => Task.FromResult(StepResult.Ok("installed")),
                    reusePreflightResult: true),
            ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("preflight-wsl", result.FailedStepId);
        Assert.Single(commands.Calls, call => call.Arguments is ["--version"]);
    }

    [Fact]
    public async Task EnsureWslPlatform_InstallsOnlyAfterReadOnlyPreflight()
    {
        var installed = false;
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] when !installed => new CommandResult(
                1,
                "",
                "Windows Subsystem for Linux is not installed. See https://aka.ms/wslinstall",
                TimeSpan.Zero,
                TimedOut: false),
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            installed = true;
            return Task.FromResult(StepResult.Ok("installed"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, installCalls);
        Assert.Equal(WslViabilityKind.Ready, ctx.WslViability?.Kind);
        Assert.Equal(3, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_StandaloneIgnoresCachedReadyResult()
    {
        var installed = false;
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] when !installed => new CommandResult(
                1,
                "",
                "Windows Subsystem for Linux is not installed. See https://aka.ms/wslinstall",
                TimeSpan.Zero,
                TimedOut: false),
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Ready,
            "stale ready",
            string.Empty);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            installed = true;
            return Task.FromResult(StepResult.Ok("installed"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, installCalls);
        Assert.Equal(3, commands.Calls.Count);
        Assert.Equal(WslViabilityKind.Ready, ctx.WslViability?.Kind);
    }

    [Fact]
    public async Task EnsureWslPlatform_StandaloneIgnoresCachedInstallableResult()
    {
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Installable,
            "stale missing",
            string.Empty);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            return Task.FromResult(StepResult.Ok("installed"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(0, installCalls);
        Assert.Equal(2, commands.Calls.Count);
        Assert.Equal(WslViabilityKind.Ready, ctx.WslViability?.Kind);
    }

    [Fact]
    public async Task PreflightWsl_ClearsCachedResultBeforeCanceledInspection()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            runWithCancellation: (_, _, _, cancellationToken) =>
                throw new OperationCanceledException(cancellationToken));
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Ready,
            "stale ready",
            string.Empty);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None));

        Assert.Null(ctx.WslViability);
    }

    [Fact]
    public async Task EnsureWslPlatform_StandaloneClearsCachedResultBeforeCanceledInspection()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            runWithCancellation: (_, _, _, cancellationToken) =>
                throw new OperationCanceledException(cancellationToken));
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Ready,
            "stale ready",
            string.Empty);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new EnsureWslPlatformStep().ExecuteAsync(ctx, CancellationToken.None));

        Assert.Null(ctx.WslViability);
    }

    [Fact]
    public async Task EnsureWslPlatform_ClearsPreflightResultBeforeCanceledInstallation()
    {
        var commands = new FakeCommandRunner(_ =>
            Fail("The cached same-run preflight result should be reused."));
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Installable,
            "current-run missing",
            string.Empty);
        using var cts = new CancellationTokenSource();
        var step = new EnsureWslPlatformStep(
            (_, cancellationToken) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cancellationToken);
            },
            reusePreflightResult: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => step.ExecuteAsync(ctx, cts.Token));
        Assert.Null(ctx.WslViability);
    }

    [Fact]
    public async Task EnsureWslPlatform_ClearsPreflightResultBeforeCanceledPostInstallInspection()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            runWithCancellation: (_, _, _, cancellationToken) =>
            {
                throw new OperationCanceledException(cancellationToken);
            });
        var ctx = CreateContext(commands: commands);
        ctx.WslViability = new WslViabilityResult(
            WslViabilityKind.Installable,
            "current-run missing",
            string.Empty);
        using var cts = new CancellationTokenSource();
        var step = new EnsureWslPlatformStep(
            (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(StepResult.Ok("installed"));
            },
            reusePreflightResult: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => step.ExecuteAsync(ctx, cts.Token));
        Assert.Null(ctx.WslViability);
    }

    [Fact]
    public async Task EnsureWslPlatform_LeavesReadyWslUnchanged()
    {
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            return Task.FromResult(StepResult.Ok("initialized"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal("WSL platform is ready.", result.Message);
        Assert.Equal(0, installCalls);
        Assert.Equal(WslViabilityKind.Ready, ctx.WslViability?.Kind);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task PreflightWsl_UninitializedPlatformIsInstallable()
    {
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => new CommandResult(
                1,
                "",
                "This application requires the Windows Subsystem for Linux Optional Component.\n" +
                "Install it by running: wsl.exe --install --no-distribution\n" +
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(WslViabilityKind.Installable, ctx.WslViability?.Kind);
        Assert.Contains("not initialized", result.Message);
        Assert.DoesNotContain(commands.Calls, call => call.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task EnsureWslPlatform_InitializesPlatformAndReinspectsReadiness()
    {
        var initialized = false;
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] when !initialized => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            ["--status"] => Ok("Default Version: 2\n"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            initialized = true;
            return Task.FromResult(StepResult.Ok("initialized"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal("WSL platform installed and verified.", result.Message);
        Assert.Equal(1, installCalls);
        Assert.Equal(WslViabilityKind.Ready, ctx.WslViability?.Kind);
        Assert.Equal(4, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_RequiresRestartWhenInitializationIsStillPending()
    {
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            return Task.FromResult(StepResult.Ok("initialized"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(result.RequiresRestart);
        Assert.Contains("restarted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reboot Windows", result.Message);
        Assert.Equal(1, installCalls);
        Assert.Equal(WslViabilityKind.Installable, ctx.WslViability?.Kind);
        Assert.Equal(4, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_PreservesRestartRequiredFromInstaller()
    {
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var installerResult = StepResult.RestartRequired("installer requires restart");
        var step = new EnsureWslPlatformStep((_, _) => Task.FromResult(installerResult));

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Same(installerResult, result);
        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(result.RequiresRestart);
        Assert.Equal("installer requires restart", result.Message);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_RequiresRestartWhenPostInstallStatusLooksLikeFirmwareFailure()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.13.0\n"),
            ["--status"] when !installed => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            ["--status"] => Ok(
                "WSL2 is unable to start since virtualization is not enabled on this machine. "
                + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
                + "and virtualization is turned on in your computer's firmware settings."),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installed = true;
            return Task.FromResult(StepResult.Ok("installed"));
        });

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.True(result.RequiresRestart);
        Assert.Contains("Reboot Windows", result.Message);
        Assert.DoesNotContain("firmware", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BIOS", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_PropagatesElevationCancellationWithoutReinspection()
    {
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
            Task.FromResult(StepResult.Fail("WSL platform install was cancelled at the elevation prompt.")));

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("elevation prompt", result.Message);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task EnsureWslPlatform_ElevationCancellationIsNotRetriedByPipeline()
    {
        var installCalls = 0;
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => new CommandResult(
                1,
                "",
                "Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
                TimeSpan.Zero,
                TimedOut: false),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);
        var step = new EnsureWslPlatformStep((_, _) =>
        {
            installCalls++;
            return Task.FromResult(
                StepResult.Fail("WSL platform install was cancelled at the elevation prompt."));
        });
        var pipeline = new SetupPipeline([step]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(step.Id, result.FailedStepId);
        Assert.Contains("elevation prompt", result.Message);
        Assert.Equal(1, installCalls);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task PreflightWsl_UnclassifiedStatusFailureFailsClosed()
    {
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Fail("Access denied"),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(WslViabilityKind.InspectionFailed, ctx.WslViability?.Kind);
        Assert.Contains("could not safely verify", result.Message);
    }

    [Fact]
    public async Task WslViabilityProbe_RefreshesCompletedInspectionWithoutRecreatingOwner()
    {
        var inspectionCount = 0;
        var probe = new WslViabilityProbe(() =>
        {
            inspectionCount++;
            return Task.FromResult(inspectionCount == 1
                ? new WslViabilityResult(
                    WslViabilityKind.InspectionFailed,
                    "Inspection failed.",
                    "Try again.")
                : new WslViabilityResult(
                    WslViabilityKind.Ready,
                    "WSL is ready.",
                    string.Empty));
        });

        WslViabilityResult first = await probe.GetAsync();
        WslViabilityResult cached = await probe.GetAsync();
        WslViabilityResult refreshed = await probe.GetAsync(refresh: true);

        Assert.Equal(WslViabilityKind.InspectionFailed, first.Kind);
        Assert.Same(first, cached);
        Assert.Equal(WslViabilityKind.Ready, refreshed.Kind);
        Assert.Equal(2, inspectionCount);
    }

    [Fact]
    public async Task WslViabilityProbe_RefreshSharesInFlightInspectionThenStartsOneNewInspection()
    {
        var firstCompletion = new TaskCompletionSource<WslViabilityResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<WslViabilityResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectionCount = 0;
        var probe = new WslViabilityProbe(() =>
            Interlocked.Increment(ref inspectionCount) switch
            {
                1 => firstCompletion.Task,
                2 => secondCompletion.Task,
                _ => throw new InvalidOperationException("Unexpected extra WSL inspection."),
            });

        Task<WslViabilityResult> first = probe.GetAsync();
        Task<WslViabilityResult> concurrentRefresh = probe.GetAsync(refresh: true);

        Assert.Same(first, concurrentRefresh);
        Assert.Equal(1, Volatile.Read(ref inspectionCount));

        firstCompletion.SetResult(new WslViabilityResult(
            WslViabilityKind.InspectionFailed,
            "Inspection failed.",
            "Try again."));
        Assert.Equal(WslViabilityKind.InspectionFailed, (await first).Kind);

        Task<WslViabilityResult> refreshed = probe.GetAsync(refresh: true);
        Task<WslViabilityResult> secondConcurrentRefresh = probe.GetAsync(refresh: true);

        Assert.NotSame(first, refreshed);
        Assert.Same(refreshed, secondConcurrentRefresh);
        Assert.Equal(2, Volatile.Read(ref inspectionCount));

        secondCompletion.SetResult(new WslViabilityResult(
            WslViabilityKind.Ready,
            "WSL is ready.",
            string.Empty));
        Assert.Equal(WslViabilityKind.Ready, (await refreshed).Kind);
        Assert.Equal(2, Volatile.Read(ref inspectionCount));
    }

    [Fact]
    public void LocalAiAvailabilityReasons_CombinesOnlyLocalAiFailures()
    {
        var result = LocalAiAvailabilityReasons.Build(
            "No qualified NVIDIA GPU was detected.",
            "The global .wslconfig file is unreadable.");

        Assert.NotNull(result);
        Assert.Contains("Hardware: No qualified NVIDIA GPU was detected.", result);
        Assert.Contains("WSL networking: The global .wslconfig file is unreadable.", result);
        Assert.DoesNotContain("Windows cannot currently start WSL2", result);
    }

    [Fact]
    public void LocalAiAvailabilityReasons_ReturnsNullWithoutLocalAiFailures()
    {
        Assert.Null(LocalAiAvailabilityReasons.Build(null, null));
    }

    [Fact]
    public async Task CreateWslInstance_UsesDirectFreshInstallAndDoesNotExportBaseDistro()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--export"));
        Assert.DoesNotContain(commands.Calls, c =>
            c.Arguments is ["--terminate", "Ubuntu-24.04"] or ["--unregister", "Ubuntu-24.04"]);

        var installCall = Assert.Single(commands.Calls, c => c.Arguments.Contains("--install"));
        Assert.Contains("--distribution", installCall.Arguments);
        Assert.Contains("Ubuntu-24.04", installCall.Arguments);
        Assert.Contains("--name", installCall.Arguments);
        Assert.Contains("OpenClawGateway", installCall.Arguments);
        Assert.Contains("--location", installCall.Arguments);
        Assert.Contains(Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway"), installCall.Arguments);
        Assert.Contains("--web-download", installCall.Arguments);
        Assert.True(ManagedDistroOwnership.HasEvidence(
            _tempDir,
            _localTempDir,
            "OpenClawGateway"));
    }

    [Fact]
    public async Task CreateWslInstance_RetriesTransientFreshDistroRootProbeTimeout()
    {
        var installed = false;
        var probeAttempts = 0;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
            {
                probeAttempts++;
                return probeAttempts == 1
                    ? new CommandResult(-1, "", "", TimeSpan.FromSeconds(30), TimedOut: true)
                    : Ok("0\nOPENCLAW_FRESH_WSL_READY\n");
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, probeAttempts);
        Assert.DoesNotContain(commands.Calls, call => call.Arguments.Contains("--unregister"));
    }

    [Fact]
    public async Task CreateWslInstance_BoundsPersistentFreshDistroRootProbeTimeouts()
    {
        var installed = false;
        var probeAttempts = 0;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
            {
                probeAttempts++;
                return new CommandResult(-1, "", "", TimeSpan.FromSeconds(30), TimedOut: true);
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Ok();
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
            {
                installed = false;
                return Ok();
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Equal(3, probeAttempts);
        Assert.Contains("could not run a root verification command", result.Message);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(90),
            ],
            commands.TimedCalls
                .Where(call => call.Arguments.SequenceEqual(
                    ["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                .Select(call => call.Timeout)
                .ToArray());
    }

    [Fact]
    public async Task CreateWslInstance_AllowsWslServiceToSettleBeforeVersionVerification()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var verboseCall = Assert.Single(
            commands.TimedCalls,
            call => call.Arguments.SequenceEqual(["--list", "--verbose"]));
        Assert.Equal(TimeSpan.FromMinutes(1), verboseCall.Timeout);
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupAvoidsGlobalShutdownWhenUnregisterSucceeds()
    {
        var listCalls = 0;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return Ok(listCalls == 1 ? "" : "OpenClawGateway\n");
            }
            if (args.Contains("--install"))
                return Fail("download failed");
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Ok();
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Ok();

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupSkipsInstallPathDeleteWhenDistroStateIsUnknown()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Fail("terminate unavailable");
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Fail("unregister unavailable");
            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.Contains("could not confirm whether distro 'OpenClawGateway' is still registered", result.Message);
        Assert.Contains("skipped deleting app-owned install path", result.Message);
        Assert.True(File.Exists(Path.Combine(installPath, "ext4.vhdx")));
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupDeletesInstallPathWhenListFailsButDistroIsAlreadyGone()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]) ||
                args.SequenceEqual(["--unregister", "OpenClawGateway"]))
            {
                return Fail("There is no distribution with the supplied name.");
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain("Partial app-owned distro cleanup also failed", result.Message);
        Assert.False(Directory.Exists(installPath));
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenTargetDistroStillExists()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still exists after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenInstallDirectoryIsDirty()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still contains files after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_RemovesStaleFileAtInstallPathBeforeInstalling()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        File.WriteAllText(installPath, "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(installPath));
        Assert.Contains(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public void WslInstallSupport_ParsesVersionAndVerboseDistroList()
    {
        Assert.True(WslInstallSupport.TryParseWslVersion("WSL version: 2.7.3.0", out var version));
        Assert.Equal(new Version(2, 7, 3, 0), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version));

        Assert.True(WslInstallSupport.TryGetDistroVersion(
            "  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n",
            "OpenClawGateway",
            out var distroVersion));
        Assert.Equal(2, distroVersion);
    }

    // Regression: wsl.exe emits UTF-16LE on some Windows builds, and localized
    // Windows changes the human-readable label around the stable WSL product token.
    [Theory]
    [InlineData("WSL version: 2.7.3.0", "2.7.3.0")]                       // English
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]                       // German / NUL-stripped UTF-16
    [InlineData("WSL-Version: 2.7.7.0\nKernelversion: 6.18.26.1-1\nWSLg-Version: 1.0.73.2\nWindows-Version: 10.0.26300.8553", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]                    // Spanish
    [InlineData("Versión de WSL: 2.7.3.0\nKernel: 5.15.0.1", "2.7.3.0")]  // Spanish with trailing lines
    [InlineData("WSL バージョン: 2.7.8.0", "2.7.8.0")]                    // Japanese-style label
    [InlineData("WSL版本: 2.7.9.0", "2.7.9.0")]                          // No separator after WSL
    public void WslInstallSupport_TryParseWslVersion_HandlesLocalizedAndHyphenatedLabels(string output, string expectedVersion)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for: {output}");
        Assert.Equal(Version.Parse(expectedVersion), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version),
            $"Expected parsed version {version} to satisfy minimum install requirement");
    }

    // Mirrors microsoft/WSL localization/strings/*/Resources.resw MessagePackageVersions.
    [Theory]
    [InlineData("cs-CZ", "Verze WSL: 2.7.3.0")]
    [InlineData("da-DK", "WSL-version: 2.7.3.0")]
    [InlineData("de-DE", "WSL-Version: 2.7.3.0")]
    [InlineData("en-GB", "WSL version: 2.7.3.0")]
    [InlineData("en-US", "WSL version: 2.7.3.0")]
    [InlineData("es-ES", "Versión de WSL: 2.7.3.0")]
    [InlineData("fi-FI", "WSL-versio: 2.7.3.0")]
    [InlineData("fr-FR", "Version WSL : 2.7.3.0")]
    [InlineData("hu-HU", "WSL-verzió: 2.7.3.0")]
    [InlineData("it-IT", "Versione WSL: 2.7.3.0")]
    [InlineData("ja-JP", "WSL バージョン: 2.7.3.0")]
    [InlineData("ko-KR", "WSL 버전: 2.7.3.0")]
    [InlineData("nb-NO", "WSL-versjon: 2.7.3.0")]
    [InlineData("nl-NL", "WSL-versie: 2.7.3.0")]
    [InlineData("pl-PL", "Wersja podsystemu WSL: 2.7.3.0")]
    [InlineData("pt-BR", "Versão do WSL: 2.7.3.0")]
    [InlineData("pt-PT", "Versão WSL: 2.7.3.0")]
    [InlineData("ru-RU", "Версия WSL: 2.7.3.0")]
    [InlineData("sv-SE", "WSL-version: 2.7.3.0")]
    [InlineData("tr-TR", "WSL sürümü: 2.7.3.0")]
    [InlineData("zh-CN", "WSL 版本: 2.7.3.0")]
    [InlineData("zh-TW", "WSL 版本： 2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_HandlesMicrosoftLocalizedPackageVersionLabels(
        string locale,
        string output)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for {locale}: {output}");
        Assert.Equal(new Version(2, 7, 3, 0), version);
    }

    [Theory]
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_NulStrippedUtf16_ParsesCorrectVersion(string raw, string expectedVersion)
    {
        // Simulate UTF-16LE NUL-byte injection then NUL-stripping.
        var utf16Encoded = string.Join("\0", raw.ToCharArray()) + "\0";
        var stripped = utf16Encoded.Replace("\0", "");
        Assert.True(WslInstallSupport.TryParseWslVersion(stripped, out var version),
            $"Expected TryParseWslVersion to succeed for NUL-stripped: {raw}");
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_IgnoresAdjacentWslAndWindowsVersionLines()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n"
            + "WSL-Version: 2.7.7.0\n";

        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version));
        Assert.Equal(new Version(2, 7, 7, 0), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_FailsWhenOnlyAdjacentComponentVersionsArePresent()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n";

        Assert.False(WslInstallSupport.TryParseWslVersion(output, out _));
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsFirmwareVirtualizationOff()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.X64,
            out var message));
        Assert.Contains("BIOS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_UsesArm64WordingOnArm64()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.Arm64,
            out var message));
        Assert.Contains("ARM64", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
        // Must not name x86-specific extensions on ARM64.
        Assert.DoesNotContain("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AMD-V", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsCanonical0x80370102Error()
    {
        // This is the actual error wsl.exe emits on modern Windows builds when
        // the Virtual Machine Platform / Hyper-V feature is disabled.
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL 2 requires an update to its kernel component.\n"
            + "For information please visit https://aka.ms/wsl2kernel\n"
            + "Error: 0x80370102 The virtual machine could not be started because a "
            + "required feature is not installed.",
            out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsUnsupportedMachineConfigurationStatus()
    {
        var status = NulSeparated("Default Version: 2\r\n\r\n"
            + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
            + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
            + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
            + "For information please visit https://aka.ms/enablevirtualization\r\n");

        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(status, Architecture.X64, out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
        Assert.Contains("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BIOS/UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reboot", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_UsesArm64WordingForUnsupportedMachineConfiguration()
    {
        var status = NulSeparated("Default Version: 2\r\n\r\n"
            + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
            + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
            + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
            + "For information please visit https://aka.ms/enablevirtualization\r\n");

        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(status, Architecture.Arm64, out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARM64", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Surface", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device-management policy", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
        Assert.DoesNotContain("BIOS", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AMD-V", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_ReturnsFalseForHealthyStatus()
    {
        Assert.False(WslInstallSupport.TryGetEnvironmentIssue(
            "Default Distribution: OpenClawGateway\nDefault Version: 2\n",
            out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenVirtualizationDisabledInFirmware()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
            {
                return new CommandResult(
                    1,
                    "",
                    "WSL2 is unable to start since virtualization is not enabled on this machine. "
                    + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
                    + "and virtualization is turned on in your computer's firmware settings.",
                    TimeSpan.Zero,
                    TimedOut: false);
            }
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(WslViabilityKind.EnvironmentBlocked, ctx.WslViability?.Kind);
        Assert.StartsWith("Windows cannot currently start WSL2.", result.Message);
        Assert.Contains("virtualization", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Local AI", result.Message, StringComparison.OrdinalIgnoreCase);
        // Don't assert on "BIOS" / "UEFI" here -- the wording flexes by host
        // CPU architecture (this test runs on either x64 or Arm64 dev boxes).
    }

    [Fact]
    public async Task PreflightWsl_VirtualizationFailureBlocksWhenLocalAiIsDisabled()
    {
        var commands = new FakeCommandRunner(args => args switch
        {
            ["--version"] => Ok("WSL version: 2.7.3.0\n"),
            ["--status"] => Ok(
                "WSL2 is unable to start since virtualization is not enabled on this machine. "
                + "Turn on virtualization in firmware settings."),
            _ => Fail($"unexpected args: {string.Join(' ', args)}"),
        });
        var ctx = CreateContext(
            new SetupConfig { LocalAi = new LocalAiConfig { Enabled = false } },
            commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(new PreflightWslStep().CanSkip(ctx));
        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(WslViabilityKind.EnvironmentBlocked, ctx.WslViability?.Kind);
        Assert.DoesNotContain("Local AI", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenWslEmitsHcsServiceNotAvailable()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok(
                    "WSL 2 requires an update to its kernel component.\n"
                    + "Error: 0x80370102 The virtual machine could not be started because a "
                    + "required feature is not installed.");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Virtual Machine Platform", result.Message);
        Assert.Contains("wsl --install --no-distribution", result.Message);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenStatusReportsUnsupportedMachineConfiguration()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.5.9.0\n");
            if (args is ["--status"])
                return Ok(NulSeparated(
                    "Default Version: 2\r\n\r\n"
                    + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
                    + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
                    + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
                    + "For information please visit https://aka.ms/enablevirtualization\r\n"));
            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Virtual Machine Platform", result.Message);
        Assert.Contains("virtualization", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task PreflightWsl_SucceedsWhenStatusOutputIsHealthy()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok("Default Distribution: OpenClawGateway\nDefault Version: 2\n");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("bad;user")]
    [InlineData("BadUser")]
    [InlineData("bad user")]
    [InlineData("bad$user")]
    public async Task ConfigureWsl_RejectsInvalidLinuxUserName(string user)
    {
        var ctx = CreateContext();
        ctx.Config.Wsl.User = user;
        ctx.DistroName = "test-distro";

        var step = new ConfigureWslInstanceStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid WSL user", result.Message);
    }

    [Fact]
    public void WslConfig_AcceptsValidLinuxUserName()
    {
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("_openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw-user_1"));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesUnmarkedLocalhostRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-localhost",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = null,
        });
        registry.Save();

        var result = await new CleanupStaleGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.GetById("external-localhost"));
    }

    [Fact]
    public async Task InstallCli_RejectsInvalidUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "not-a-url" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Theory]
    [InlineData("gateway.auth.token")]
    [InlineData("gateway_nodes-allowCommands")]
    [InlineData("a.b_c-1")]
    public void ConfigureGateway_AcceptsSafeExtraConfigKeys(string key)
    {
        Assert.True(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Theory]
    [InlineData("bad key")]
    [InlineData("bad$key")]
    [InlineData("bad;key")]
    [InlineData("bad\nkey")]
    public void ConfigureGateway_RejectsUnsafeExtraConfigKeys(string key)
    {
        Assert.False(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Fact]
    public void ConfigureGateway_DefaultsReloadModeToHybrid()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig(),
            18789,
            "'[]'");

        Assert.Contains("openclaw config set gateway.reload.mode hybrid", commands);
    }

    [Fact]
    public void ConfigureGateway_EffectiveReloadModeUsesExtraConfigOverride()
    {
        var config = new GatewayConfig
        {
            ReloadMode = "hybrid",
            ExtraConfig = new Dictionary<string, string>
            {
                ["gateway.reload.mode"] = "off",
            },
        };

        Assert.Equal("off", ConfigureGatewayStep.GetEffectiveReloadMode(config));
    }

    [Fact]
    public void SetupWizard_StartParametersDisableDaemonInstallation()
    {
        var json = JsonSerializer.Serialize(SetupWizardRunner.BuildWizardStartParameters());
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("installDaemon").GetBoolean());
    }

    [Fact]
    public void SetupWizard_RecognizesLegacyInstallDaemonSchemaRejection()
    {
        Assert.True(SetupWizardRunner.IsInstallDaemonParameterUnsupported(
            new InvalidOperationException(
                "invalid wizard.start params: at root: unexpected property 'installDaemon'")));
        Assert.False(SetupWizardRunner.IsInstallDaemonParameterUnsupported(
            new InvalidOperationException("wizard.start unavailable during gateway restart")));
    }

    [Fact]
    public void SetupWizard_TerminalTuiSigtermAfterFinalStepCompletesWithoutCancel()
    {
        var decision = SetupWizardRunner.DecideTerminalWizardError(
            payloadIsTerminal: true,
            "Error: TUI exited from signal SIGTERM",
            answeredFinalWizardStep: true);

        Assert.True(decision.Result.IsSuccess, decision.Result.Message);
        Assert.True(decision.MarksWizardCompleted);
        Assert.Contains("hosted wizard TUI after the final step", decision.Result.Message);
        Assert.Contains("Error: TUI exited from signal SIGTERM", decision.LogWarning);
    }

    [Theory]
    // Early SIGTERM before the authoritative final step was answered.
    [InlineData(true, "Error: TUI exited from signal SIGTERM", false)]
    // Terminal-looking error on a non-terminal payload.
    [InlineData(false, "Error: TUI exited from signal SIGTERM", true)]
    // Inexact SIGTERM-like errors.
    [InlineData(true, "Error: TUI exited from signal SIGKILL", true)]
    [InlineData(true, "TUI exited from signal SIGTERM", true)]
    [InlineData(true, "Error: TUI exited from signal SIGTERM then the gateway died", true)]
    // Unrelated terminal failures.
    [InlineData(true, "PROTOCOL_MISMATCH", true)]
    [InlineData(true, "Wizard returned error status.", true)]
    public void SetupWizard_TerminalWizardErrorsStayFatalAndDoNotSuppressCancel(
        bool payloadIsTerminal,
        string error,
        bool answeredFinalWizardStep)
    {
        var decision = SetupWizardRunner.DecideTerminalWizardError(
            payloadIsTerminal,
            error,
            answeredFinalWizardStep);

        Assert.False(decision.Result.IsSuccess);
        Assert.False(decision.MarksWizardCompleted);
        Assert.Null(decision.LogWarning);
        Assert.Equal($"Gateway wizard failed: {error}", decision.Result.Message);
    }

    [Fact]
    public void SetupWizard_KnownFinalizationPromptBugStillCompletesWithoutFinalStep()
    {
        var decision = SetupWizardRunner.DecideTerminalWizardError(
            payloadIsTerminal: true,
            "TypeError: this.prompt is not a function",
            answeredFinalWizardStep: false);

        Assert.True(decision.Result.IsSuccess, decision.Result.Message);
        Assert.True(decision.MarksWizardCompleted);
        Assert.Equal(
            "Gateway wizard completed with non-fatal finalization prompt warning",
            decision.Result.Message);
    }

    [Fact]
    public async Task StartGateway_RestartUsesRestartCommandAndWaitsForHealth()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("openclaw gateway restart") => Ok(),
                var value when value.Contains("curl -s") => Ok("200"),
                _ => Fail($"Unexpected command: {command}"),
            });
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";

        var result =
            await StartGatewayStep.RestartAndWaitForHealthAsync(
                ctx,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(
            commands.WslCalls,
            call => call.Command.Contains("ss -tlnp"));
        Assert.Contains(
            commands.WslCalls,
            call => call.Command.Contains("openclaw gateway restart"));
    }

    [Fact]
    public async Task SetupWizard_SuspendReloadModeRunsBeforeHealthVerification()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("config set gateway.reload.mode off") => Ok(),
                var value when value.Contains("curl -s") => Ok("200"),
                _ => Fail($"Unexpected command: {command}"),
            });
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);

        var result = await new SetupWizardRunner(ctx).SuspendReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, commands.WslCalls.Count);
        Assert.Contains(
            "config set gateway.reload.mode off",
            commands.WslCalls[0].Command);
        Assert.Contains("curl -s", commands.WslCalls[1].Command);
    }

    [Fact]
    public async Task SetupWizard_RestoreReloadModeRestartsAndVerifiesGateway()
    {
        var commands = CreateReloadRestorationRunner();
        var ctx = CreateContext(
            new SetupConfig
            {
                Gateway = new GatewayConfig { ReloadMode = "hybrid" },
            },
            commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);

        var result = await new SetupWizardRunner(ctx).RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        AssertReloadRestorationCompleted(commands);
    }

    [Fact]
    public async Task SetupWizard_RestoreReloadModeRetriesExactStartupMigrationLeaseContention()
    {
        var restoreAttempts = 0;
        var delays = new List<TimeSpan>();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("config set gateway.reload.mode 'hybrid'") =>
                    ++restoreAttempts < 3
                        ? Fail($"{SetupWizardRunner.StartupMigrationLeaseDiagnostic} retry after the other OpenClaw process finishes. (held by pid 266)")
                        : Ok(),
                var value when value.Contains("openclaw gateway restart") => Ok(),
                var value when value.Contains("curl -s") => Ok("200"),
                _ => Fail($"Unexpected command: {command}"),
            });
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);
        var runner = new SetupWizardRunner(
            ctx,
            (delay, cancellationToken) =>
            {
                Assert.False(cancellationToken.CanBeCanceled);
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await runner.RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(3, restoreAttempts);
        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(250), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[1]);
        Assert.Single(
            commands.WslCalls,
            call => call.Command.Contains("openclaw gateway restart"));
        AssertReloadRestorationCompleted(commands);
    }

    [Fact]
    public async Task SetupWizard_OrchestratorRestoresAfterSuccessfulWizard()
    {
        var commands = CreateReloadRestorationRunner();
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);
        var runner = new SetupWizardRunner(ctx);

        var result = await runner.RunWithReloadRestorationAsync(() =>
        {
            runner.MarkReloadSuspended();
            return Task.FromResult(StepResult.Ok("wizard complete"));
        });

        Assert.True(result.IsSuccess, result.Message);
        AssertReloadRestorationCompleted(commands);
    }

    [Fact]
    public async Task SetupWizard_OrchestratorRestoresThroughLeaseContentionBeforePropagatingCancellation()
    {
        var restoreAttempts = 0;
        var delayTokens = new List<CancellationToken>();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("config set gateway.reload.mode 'hybrid'") =>
                    ++restoreAttempts == 1
                        ? Fail($"{SetupWizardRunner.StartupMigrationLeaseDiagnostic} retry after the other OpenClaw process finishes.")
                        : Ok(),
                var value when value.Contains("openclaw gateway restart") => Ok(),
                var value when value.Contains("curl -s") => Ok("200"),
                _ => Fail($"Unexpected command: {command}"),
            });
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);
        var runner = new SetupWizardRunner(
            ctx,
            (_, cancellationToken) =>
            {
                delayTokens.Add(cancellationToken);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunWithReloadRestorationAsync(async () =>
            {
                runner.MarkReloadSuspended();
                await Task.Yield();
                throw new OperationCanceledException();
            }));

        Assert.Equal(2, restoreAttempts);
        Assert.Single(delayTokens);
        Assert.False(delayTokens[0].CanBeCanceled);
        AssertReloadRestorationCompleted(commands);
    }

    [Fact]
    public async Task SetupWizard_OrchestratorDoesNotRetryUnrelatedRestorationFailure()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
                command.Contains("config set gateway.reload.mode 'hybrid'")
                    ? Fail("restore failed")
                    : Fail($"Unexpected command: {command}"));
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        var runner = new SetupWizardRunner(ctx);

        var result = await runner.RunWithReloadRestorationAsync(() =>
        {
            runner.MarkReloadSuspended();
            return Task.FromResult(StepResult.Ok("wizard complete"));
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Failed to restore gateway.reload.mode", result.Message);
        Assert.Single(
            commands.WslCalls,
            call => call.Command.Contains("config set gateway.reload.mode 'hybrid'"));
        Assert.DoesNotContain(
            commands.WslCalls,
            call => call.Command.Contains("openclaw gateway restart"));
    }

    [Fact]
    public async Task SetupWizard_LeaseContentionExhaustionPreservesWizardAndRestorationFailures()
    {
        var restoreAttempts = 0;
        var timeProvider = new ManualTimeProvider();
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
                command.Contains("config set gateway.reload.mode 'hybrid'")
                    ? FailWithStdout(
                        $"{SetupWizardRunner.StartupMigrationLeaseDiagnostic} retry after the other OpenClaw process finishes. (held by pid 247)")
                    : Fail($"Unexpected command: {command}"));
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        var runner = new SetupWizardRunner(
            ctx,
            (delay, _) =>
            {
                restoreAttempts++;
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            },
            timeProvider);

        var result = await runner.RunWithReloadRestorationAsync(() =>
        {
            runner.MarkReloadSuspended();
            return Task.FromResult(StepResult.Fail("wizard failed"));
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Failed to restore gateway.reload.mode", result.Message);
        Assert.Contains(SetupWizardRunner.StartupMigrationLeaseDiagnostic, result.Message);
        Assert.Contains("wizard failed", result.Message);
        Assert.Equal(10, restoreAttempts);
        Assert.Equal(
            11,
            commands.WslCalls.Count(
                call => call.Command.Contains("config set gateway.reload.mode 'hybrid'")));
        Assert.Equal(TimeSpan.FromSeconds(14.5), timeProvider.Elapsed);
        Assert.All(
            commands.WslCalls.Where(
                call => call.Command.Contains("config set gateway.reload.mode 'hybrid'")),
            call => Assert.True(call.Timeout >= TimeSpan.FromMilliseconds(500)));
        Assert.DoesNotContain(
            commands.WslCalls,
            call => call.Command.Contains("openclaw gateway restart"));
    }

    [Fact]
    public async Task SetupWizard_OrchestratorPreservesWizardFailureAfterRestoration()
    {
        var commands = CreateReloadRestorationRunner();
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);
        var runner = new SetupWizardRunner(ctx);

        var result = await runner.RunWithReloadRestorationAsync(() =>
        {
            runner.MarkReloadSuspended();
            return Task.FromResult(StepResult.Fail("wizard failed"));
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("wizard failed", result.Message);
        AssertReloadRestorationCompleted(commands);
    }

    [Fact]
    public async Task SetupWizard_SuspendHealthFailureStillRestoresReloadMode()
    {
        var restoring = false;
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("config set gateway.reload.mode off") => Ok(),
                var value when value.Contains("config set gateway.reload.mode 'hybrid'") =>
                    SetRestoring(),
                var value when value.Contains("openclaw gateway restart") => Ok(),
                var value when value.Contains("curl -s") && restoring => Ok("200"),
                var value when value.Contains("curl -s") => Fail("not ready"),
                var value when value.Contains("systemctl --user status") => Ok(),
                var value when value.Contains("journalctl --user-unit") => Ok(),
                _ => Fail($"Unexpected command: {command}"),
            });
        var config = new SetupConfig();
        config.Gateway.HealthTimeoutSeconds = 1;
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        TrustManagedEndpoint(ctx);
        var runner = new SetupWizardRunner(ctx);

        var result = await runner.RunWithReloadRestorationAsync(
            () => runner.SuspendReloadModeAsync());

        Assert.False(result.IsSuccess);
        Assert.Contains("Gateway did not become healthy", result.Message);
        AssertReloadRestorationCompleted(commands);
        return;

        CommandResult SetRestoring()
        {
            restoring = true;
            return Ok();
        }
    }

    [Fact]
    public async Task SetupWizard_RestoreReloadModeFailsClosedOnUnknownListener()
    {
        var commands = CreateReloadRestorationRunner();
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.UnknownListener,
                ctx.Config.GatewayPort,
                Detail: "unknown owner"));

        var result = await new SetupWizardRunner(ctx).RestoreReloadModeAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("ownership verification failed", result.Message);
    }

    [Theory]
    [InlineData("2026.6.11", ConfigureGatewayStep.LegacyNodeCommandsAllowKey, ConfigureGatewayStep.NodeCommandsAllowKey)]
    [InlineData("2026.6.34", ConfigureGatewayStep.LegacyNodeCommandsAllowKey, ConfigureGatewayStep.NodeCommandsAllowKey)]
    [InlineData("2026.7.2", ConfigureGatewayStep.NodeCommandsAllowKey, ConfigureGatewayStep.LegacyNodeCommandsAllowKey)]
    [InlineData("2026.7.2-1", ConfigureGatewayStep.NodeCommandsAllowKey, ConfigureGatewayStep.LegacyNodeCommandsAllowKey)]
    public void ConfigureGateway_UsesVersionedNodeCommandsAllowKey(
        string gatewayVersion,
        string expectedKey,
        string rejectedKey)
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Version = gatewayVersion },
            18789,
            "'[\"system.which\"]'");

        Assert.Contains($"openclaw config set {expectedKey} '[\"system.which\"]'", commands);
        Assert.DoesNotContain($"openclaw config set {rejectedKey} ", commands);
    }

    [Theory]
    [InlineData("2026.6.34", ConfigureGatewayStep.NodeCommandsAllowKey, ConfigureGatewayStep.LegacyNodeCommandsAllowKey)]
    [InlineData("2026.7.2", ConfigureGatewayStep.LegacyNodeCommandsAllowKey, ConfigureGatewayStep.NodeCommandsAllowKey)]
    public void ConfigureGateway_NormalizesNodeCommandsAllowOverrideToTargetSchema(
        string gatewayVersion,
        string configuredKey,
        string expectedKey)
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Version = gatewayVersion,
                ExtraConfig = new Dictionary<string, string>
                {
                    [configuredKey] = "[\"camera.snap\"]"
                }
            },
            18789,
            "'[\"system.which\"]'");

        Assert.Contains($"openclaw config set {expectedKey} '[\"camera.snap\"]'", commands);
        Assert.DoesNotContain($"openclaw config set {configuredKey} ", commands);
    }

    [Fact]
    public async Task ConfigureGateway_RejectsConflictingNodeCommandsAllowOverrides()
    {
        var context = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "2026.7.2",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.LegacyNodeCommandsAllowKey] = "[]",
                    [ConfigureGatewayStep.NodeCommandsAllowKey] = "[]"
                }
            }
        });

        var result = await new ConfigureGatewayStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("cannot define both", result.Message);
    }

    [Fact]
    public void ConfigureGateway_AddsDevicePairPublicUrlForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'http://127.0.0.1:18789'",
            commands);
    }

    [Fact]
    public void ConfigureGateway_RefreshesBundledPluginRegistryBeforeWritingPluginConfig()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig(),
            18789,
            "'[]'");

        var refreshIndex = commands.IndexOf(
            "openclaw plugins registry --refresh",
            StringComparison.Ordinal);
        var devicePairIndex = commands.IndexOf(
            "openclaw config set plugins.entries.device-pair.enabled true",
            StringComparison.Ordinal);

        Assert.True(refreshIndex >= 0);
        Assert.True(devicePairIndex > refreshIndex);
    }

    // Issue: device-pair plugin must be enabled, not just configured. Otherwise
    // OAuth providers (Codex, etc.) hang at scope-upgrade and never emit auth URLs.
    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_TailscaleServeIsRootOwnedAndKeepsTailscaleAuthOptIn()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'",
            new TailscaleConfig { Enabled = true });

        Assert.Contains("openclaw config set gateway.tailscale.mode off", commands);
        Assert.DoesNotContain("gateway.tailscale.mode serve", commands);
        Assert.DoesNotContain("gateway.tailscale.resetOnExit", commands);
        Assert.Contains("openclaw config set gateway.auth.allowTailscale false", commands);
        Assert.DoesNotContain("http://127.0.0.1:18789", commands);
    }

    [Fact]
    public void ConfigureGateway_EnablesTailscaleIdentityAuthOnlyWhenRequested()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'",
            new TailscaleConfig { Enabled = true, TrustTailscaleAuth = true });

        Assert.Contains("openclaw config set gateway.auth.allowTailscale true", commands);
    }

    [Fact]
    public async Task TailscaleTransportWithoutIdentityTrust_PreservesTokenAndDeviceCredentialsForPairing()
    {
        var config = new SetupConfig
        {
            GatewayPort = GetFreeTcpPort(),
            Tailscale = new TailscaleConfig { Enabled = true, TrustTailscaleAuth = false }
        };
        var ctx = CreateContext(config);
        ctx.SharedGatewayToken = "shared-token";
        ctx.BootstrapToken = "bootstrap-token";

        var gatewayConfig = ConfigureGatewayStep.BuildConfigCommands(
            config.Gateway,
            config.GatewayPort,
            "'[]'",
            config.Tailscale);
        Assert.Equal("shared-token", SetupPairingCredentialPolicy.ResolveInitialPairingToken(ctx));
        ctx.SharedGatewayToken = null;
        Assert.Equal("bootstrap-token", SetupPairingCredentialPolicy.ResolveInitialPairingToken(ctx));
        ctx.SharedGatewayToken = "shared-token";
        var pairResult = await new PairOperatorStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(pairResult.IsSuccess);
        Assert.Contains("gateway.auth.allowTailscale false", gatewayConfig);
        Assert.NotNull(ctx.GatewayRecordId);

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        var record = Assert.IsType<GatewayRecord>(registry.GetById(ctx.GatewayRecordId!));
        Assert.Equal("Tailscale (OpenClawGateway)", record.FriendlyName);
        Assert.Equal("shared-token", record.SharedGatewayToken);
        Assert.Equal("bootstrap-token", record.BootstrapToken);

        var identityPath = registry.GetIdentityDirectory(record.Id);
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "operator-device-token");
        identity.StoreDeviceTokenForRole("node", "node-device-token");

        var credentials = new CredentialResolver(DeviceIdentityFileReader.Instance);
        Assert.Equal("operator-device-token", credentials.ResolveOperator(record, identityPath)!.Token);
        Assert.Equal("node-device-token", credentials.ResolveNode(record, identityPath)!.Token);
    }

    [Fact]
    public void TailscalePolicy_ParsesAuthorizationUrlsAndOnlyAcceptsGatewayServeProxy()
    {
        var url = TailscaleSetupPolicy.TryReadAuthorizationUrl("To authenticate, visit https://login.tailscale.com/a/abc_123-now");
        const string expectedServeStatus = """
            {
              "TCP": { "443": { "HTTPS": true } },
              "Web": {
                "openclaw.example.ts.net:443": {
                  "Handlers": {
                    "/": { "Proxy": "http://127.0.0.1:18789" }
                  }
                }
              }
            }
            """;
        const string wrongBackendStatus = """
            {
              "Web": {
                "openclaw.example.ts.net:443": {
                  "Handlers": {
                    "/": { "Proxy": "http://127.0.0.1:9999" }
                  }
                }
              }
            }
            """;
        const string unrelatedPortStatus = """
            {
              "TCP": { "18789": { "HTTPS": true } },
              "Web": {
                "openclaw.example.ts.net:443": {
                  "Handlers": {
                    "/": { "Proxy": "http://127.0.0.1:9999" }
                  }
                }
              }
            }
            """;
        const string funnelStatus = """
            {
              "AllowFunnel": { "openclaw.example.ts.net:443": true },
              "Web": {
                "openclaw.example.ts.net:443": {
                  "Handlers": {
                    "/": { "Proxy": "http://127.0.0.1:18789" }
                  }
                }
              }
            }
            """;

        Assert.Equal("https://login.tailscale.com/a/abc_123-now", url!.AbsoluteUri);
        Assert.True(TailscaleSetupPolicy.ServeStatusRoutesToPort(expectedServeStatus, 18789));
        Assert.False(TailscaleSetupPolicy.ServeStatusRoutesToPort(wrongBackendStatus, 18789));
        Assert.False(TailscaleSetupPolicy.ServeStatusRoutesToPort(unrelatedPortStatus, 18789));
        Assert.False(TailscaleSetupPolicy.ServeStatusEnablesFunnel(expectedServeStatus, 18789));
        Assert.True(TailscaleSetupPolicy.ServeStatusEnablesFunnel(funnelStatus, 18789));
    }

    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginWhenPublicUrlOverridden()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "lan",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotEnableDevicePairWhenNoPublicUrlAvailable()
    {
        // LAN bind with no operator-supplied publicUrl: we don't know where the plugin
        // would be reachable, so don't enable it; preserves the prior behavior.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");

        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled",
            commands);
    }

    [Fact]
    public void ConfigureGateway_RespectsExplicitDevicePairEnabledOverride()
    {
        // If the operator explicitly sets the enabled flag via ExtraConfig, the
        // ExtraConfig loop writes it and we don't append a duplicate.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairEnabledKey] = "false",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled 'false'",
            commands);
        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotOverrideExplicitDevicePairPublicUrl()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.DoesNotContain("'http://127.0.0.1:18789'", commands);
        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'https://gateway.example.test'",
            commands);
    }

    // Characterization (PR 3 WslShellQuoting migration): the ExtraConfig value is emitted
    // as a fully-wrapped POSIX token, so an embedded single quote must close-escape-reopen
    // ('\'') and remain single-quoted. Pins the generated command byte-for-byte.
    [Fact]
    public void ConfigureGateway_QuotesExtraConfigValueWithEmbeddedSingleQuote()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "lan",
                ExtraConfig = new Dictionary<string, string>
                {
                    ["gateway.custom.note"] = "a'b",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set gateway.custom.note 'a'\\''b'",
            commands);
    }

    [Fact]
    public async Task ConfigureGateway_UsesExtendedTimeoutForWslConfig()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok("GATEWAY_CONFIGURED"));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var wslCall = Assert.Single(commands.WslCalls);
        Assert.Equal(
            ConfigureGatewayStep.ComputeConfigurationTimeout(wslCall.Command),
            wslCall.Timeout);
        Assert.True(wslCall.Timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
    }

    [Fact]
    public async Task ConfigureGateway_ReturnsTimeoutSpecificFailure()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, timeout) => new CommandResult(-1, "", "", timeout, TimedOut: true));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        var message = Assert.IsType<string>(result.Message);
        Assert.Contains("Gateway configuration timed out after", message);
        Assert.DoesNotContain("exit -1", message);
    }

    [Fact]
    public void ComputeConfigurationTimeout_ScalesWithConfigCommandCount()
    {
        // Each `openclaw config set` pays a cold Node start inside WSL. As more keys are
        // configured the budget must grow, otherwise the step silently regresses toward a
        // timeout (the failure mode the fixed 120s cap only partially closed).
        var fewCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");
        var manyCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    ["gateway.extra.one"] = "1",
                    ["gateway.extra.two"] = "2",
                    ["gateway.extra.three"] = "3",
                    ["gateway.extra.four"] = "4",
                },
            },
            18789,
            "'[]'");

        var fewTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(fewCommands);
        var manyTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(manyCommands);

        Assert.True(
            manyTimeout > fewTimeout,
            $"Timeout should grow with config command count; few={fewTimeout}, many={manyTimeout}");
    }

    [Fact]
    public void ComputeConfigurationTimeout_NeverBelowFloor()
    {
        // A minimal config set must still receive the safety floor, never base + one.
        var timeout = ConfigureGatewayStep.ComputeConfigurationTimeout(
            "openclaw config set gateway.mode local");

        Assert.True(timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
    }

    [Theory]
    [InlineData("""{"bootstrapToken":"boot-token"}""", "boot-token", "bootstrapToken")]
    [InlineData("""{"setupCode":"setup-code"}""", "setup-code", "setupCode")]
    public void MintBootstrapToken_ReadsSupportedQrJsonShapes(string json, string expectedToken, string expectedSource)
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken(json, out var token, out var source);

        Assert.True(parsed);
        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void MintBootstrapToken_RejectsQrJsonWithoutUsableBootstrapCredential()
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken("""{"gatewayUrl":"ws://127.0.0.1:18789"}""", out var token, out var source);

        Assert.False(parsed);
        Assert.Null(token);
        Assert.Null(source);
    }

    [Fact]
    public async Task InstallCli_RejectsFtpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "ftp://files.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void BuildReplacementSummary_NoExistingConfig_StatesNothingAffected()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false,
            LocalGatewayId: null,
            LocalGatewayUrl: null,
            HasDistro: false,
            HasDistroDataDirectory: false,
            DistroIsAppOwned: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("No existing configuration will be affected", summary);
    }

    [Theory]
    [InlineData("OpenClawGateway\nUbuntu-24.04\n", true)]
    [InlineData("Ubuntu-24.04\n", false)]
    public void ExistingConfigDetector_InterpretsSuccessfulDistroList(string stdout, bool expected)
    {
        var result = new CommandResult(0, stdout, "", TimeSpan.Zero, TimedOut: false);

        Assert.Equal(expected, ExistingConfigDetector.InterpretDistroList(result, "OpenClawGateway"));
    }

    [Fact]
    public void ExistingConfigDetector_TreatsUnavailableWslAsNoDistro()
    {
        var result = new CommandResult(1, "", "WSL is not installed. See https://aka.ms/wslinstall", TimeSpan.Zero, false);

        Assert.False(ExistingConfigDetector.InterpretDistroList(result, "OpenClawGateway"));
    }

    [Fact]
    public void ExistingConfigDetector_KeepsConclusiveUnavailableAnswerWhenRunAlsoTimedOut()
    {
        // A run can time out after wsl.exe already reported that WSL is not installed.
        // That output still proves no distro can exist, so the answer stays usable
        // instead of failing closed and dead-ending the setup flow.
        var result = new CommandResult(
            -1,
            "",
            "WSL is not installed. See https://aka.ms/wslinstall",
            TimeSpan.FromSeconds(5),
            TimedOut: true);

        Assert.False(ExistingConfigDetector.InterpretDistroList(result, "OpenClawGateway"));
    }

    [Theory]
    [InlineData("Error code: Wsl/WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED")]
    [InlineData("This application requires the Windows Subsystem for Linux Optional Component.")]
    [InlineData("Optional components needed to run WSL are not installed.")]
    [InlineData("Error: 0x8007019e")]
    public void ExistingConfigDetector_TreatsUninitializedWslAsNoDistro(string error)
    {
        var result = new CommandResult(1, "", error, TimeSpan.Zero, TimedOut: false);

        Assert.False(ExistingConfigDetector.InterpretDistroList(result, "OpenClawGateway"));
    }

    [Theory]
    [InlineData(true, 1, "")]
    [InlineData(false, 1, "unexpected failure")]
    public void ExistingConfigDetector_FailsClosedWhenDistroStateIsUnknown(bool timedOut, int exitCode, string stderr)
    {
        var result = new CommandResult(exitCode, "", stderr, TimeSpan.Zero, timedOut);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExistingConfigDetector.InterpretDistroList(result, "OpenClawGateway"));
        Assert.Contains("could not safely inspect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReplacementSummary_LocalGatewayAndDistro_MentionsReplacement()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: true,
            HasDistroDataDirectory: true,
            DistroIsAppOwned: true,
            DistroName: "OpenClaw",
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("WSL distro 'OpenClaw' will be deleted and recreated", summary);
        Assert.Contains("Local gateway record will be replaced", summary);
    }

    [Fact]
    public void BuildReplacementSummary_PreservedGateways_MentionsPreservation()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            HasDistroDataDirectory: false,
            DistroIsAppOwned: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 2,
            PreservedGatewayNames: ["Remote Gateway", "SSH Tunnel"]);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("will NOT be affected", summary);
        Assert.Contains("Remote Gateway", summary);
        Assert.Contains("SSH Tunnel", summary);
    }

    [Fact]
    public void BuildReplacementSummary_IdentityFiles_MentionsRegeneration()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            HasDistroDataDirectory: false,
            DistroIsAppOwned: false,
            DistroName: null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("Device identity files for the local gateway will be regenerated", summary);
    }

    [Fact]
    public void BuildReplacementSummary_UnownedDistro_RequiresExplicitReplacement()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false,
            LocalGatewayId: null,
            LocalGatewayUrl: null,
            HasDistro: true,
            HasDistroDataDirectory: true,
            DistroIsAppOwned: false,
            DistroName: "OpenClawGateway",
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("not proven to be app-owned", summary);
        Assert.Contains("permanently delete and recreate", summary);
        Assert.True(ExistingConfigDetector.RequiresDestructiveConfirmation(config));
    }

    [Fact]
    public void BuildReplacementSummary_UnownedOrphanDirectory_NamesTarget()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false,
            LocalGatewayId: null,
            LocalGatewayUrl: null,
            HasDistro: false,
            HasDistroDataDirectory: true,
            DistroIsAppOwned: false,
            DistroName: "OpenClawGateway",
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("WSL data for 'OpenClawGateway'", summary);
        Assert.True(ExistingConfigDetector.RequiresDestructiveConfirmation(config));
    }

    [Fact]
    public void RedactTokens_RedactsThirtyTwoCharHexString()
    {
        const string token = "1234567890abcdef1234567890abcdef";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal("12345678…[REDACTED]", result);
    }

    [Fact]
    public void RedactTokens_DoesNotRedactShortHexString()
    {
        const string token = "1234567890abcdef1234567890abcde";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal(token, result);
    }

    [Fact]
    public void RedactTokens_LeavesNormalTextUnchanged()
    {
        const string text = "gateway started successfully";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal(text, result);
    }

    [Fact]
    public void RedactTokens_RedactsEmbeddedTokenOnly()
    {
        const string text = "token=1234567890abcdef1234567890abcdef status=ok";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal("token=12345678…[REDACTED] status=ok", result);
    }

    // Keepalive marker/identity tests moved to KeepaliveProcessManagerTests.cs — this logic now
    // lives in KeepaliveProcessManager, not StartKeepaliveStep (see setup-keepalive-process-manager
    // in docs/ARCHITECTURE.md).

    [Fact]
    public async Task AutoApprovePairing_ReturnsTerminalForDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairOperatorStep.AutoApprovePairing(ctx, "device-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApprovePairing_KeepsOtherMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairOperatorStep.AutoApprovePairing(ctx, "device-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Device approval failed", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_ReturnsTerminalWhenPendingListReportsDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, requestId: null, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_KeepsOtherPendingListMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, requestId: null, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not list pending node pairing requests", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_ReturnsTerminalWhenApproveReportsDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, "node-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_KeepsOtherApproveMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, "node-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Node approval failed", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    // ─── Bind validation ───

    [Fact]
    public async Task ConfigureGateway_RejectsInvalidBind()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { Bind = "0.0.0.0" }
        });
        ctx.DistroName = "test-distro";

        var step = new ConfigureGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid Gateway.Bind", result.Message);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("lan")]
    public void ConfigureGateway_AcceptsValidBindValues(string bind)
    {
        var gw = new GatewayConfig { Bind = bind };
        Assert.True(gw.Bind is "loopback" or "lan");
    }

    // ─── Secure defaults ───

    [Fact]
    public void DefaultConfig_HasSecureDefaults()
    {
        var config = new SetupConfig();

        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.True(config.Wsl.Systemd);
        Assert.False(config.Wsl.Interop);
        Assert.False(config.Wsl.AppendWindowsPath);
        Assert.False(config.Wsl.Automount);
        Assert.False(config.Wsl.MountFsTab);
    }

    [Fact]
    public void DefaultConfig_NoPairingScopeFields()
    {
        var props = typeof(PairingConfig).GetProperties();
        var scopeProps = props.Where(p => p.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(scopeProps);
    }

    [Fact]
    public async Task ValidateWslLockdown_RetriesWslConfReadAfterStartupTimeout()
    {
        var catAttempts = 0;
        var ctx = CreateContext(commands: new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command == "cat /etc/wsl.conf")
                {
                    catAttempts++;
                    return catAttempts == 1
                        ? TimedOut()
                        : Ok("""
                            [boot]
                            systemd=true

                            [automount]
                            enabled=false
                            mountFsTab=false

                            [interop]
                            enabled=false
                            appendWindowsPath=false

                            [user]
                            default=openclaw
                            """);
                }

                if (command.Contains("LOCKDOWN_VALID", StringComparison.Ordinal))
                    return Ok("LOCKDOWN_VALID\n");

                return Fail("unexpected WSL command");
            }));
        ctx.DistroName = "test-distro";

        var result = await new ValidateWslLockdownStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, catAttempts);
    }

    // ─── PairOperatorStep: Windows-side gateway health check ───

    [Fact]
    public async Task PairOperatorStep_FailsWhenGatewayNotReachableFromWindows()
    {
        // Allocate a port and immediately release it so nothing is listening on it.
        var port = GetFreeTcpPort();

        var config = new SetupConfig { GatewayPort = port };
        var ctx = CreateContext(config);
        ctx.SharedGatewayToken = "test-shared-token";

        var step = new PairOperatorStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("not reachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PairOperatorStep_WhenSavedIdentityIsCorrupt_ReturnsTerminalWithoutMutation()
    {
        var config = new SetupConfig { GatewayPort = GetFreeTcpPort() };
        var context = CreateContext(config);
        context.GatewayUrl = $"ws://127.0.0.1:{config.GatewayPort}";
        context.SharedGatewayToken = "shared-token";
        context.DistroName = "OpenClawGateway";
        var registry = new GatewayRegistry(_tempDir);
        var record = registry.AddOrUpdate(new GatewayRecord
        {
            Id = "identity-failure",
            Url = context.GatewayUrl,
            SharedGatewayToken = context.SharedGatewayToken,
            IsLocal = true,
            SetupManagedDistroName = context.DistroName
        });
        registry.SetActive(record.Id);
        registry.Save();
        context.GatewayRecordId = record.Id;
        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(identityDirectory);
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        File.WriteAllText(
            identityPath,
            JsonSerializer.Serialize(new
            {
                PrivateKeyBase64 = Convert.ToBase64String(new byte[31]),
                PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
                DeviceId = new string('0', 64),
                Algorithm = "Ed25519"
            }));
        var originalBytes = File.ReadAllBytes(identityPath);

        var result = await new PairOperatorStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, result.Message);
        Assert.IsType<DeviceIdentityLoadException>(result.Error);
        Assert.Equal(originalBytes, File.ReadAllBytes(identityPath));
        Assert.Empty(Directory.GetFiles(identityDirectory, ".device-key-ed25519.json.*.tmp"));
    }

    [Fact]
    public async Task VerifyEndToEnd_WhenSavedIdentityIsCorrupt_ReturnsTerminalWithoutMutation()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("gateway status", StringComparison.Ordinal)
                ? Ok("""{"status":"running"}""")
                : Fail($"Unexpected WSL command: {command}"));
        var context = CreateContext(commands: commands);
        context.DistroName = "test-distro";

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        var record = registry.AddOrUpdate(new GatewayRecord
        {
            Id = "verify-corrupt-identity",
            Url = context.GatewayUrl!,
            IsLocal = true,
        });
        registry.Save();
        context.GatewayRecordId = record.Id;

        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(identityDirectory);
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        File.WriteAllText(identityPath, "{");
        var originalBytes = File.ReadAllBytes(identityPath);

        var result = await new VerifyEndToEndStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, result.Message);
        Assert.IsType<DeviceIdentityLoadException>(result.Error);
        Assert.Equal(originalBytes, File.ReadAllBytes(identityPath));
        Assert.Empty(Directory.GetFiles(identityDirectory, ".device-key-ed25519.json.*.tmp"));
    }

    [Fact]
    public async Task SetupWizardRunner_WhenSavedIdentityIsCorrupt_ReturnsTerminalWithoutStartingFreshPairing()
    {
        var context = CreateContext();
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        var record = registry.AddOrUpdate(new GatewayRecord
        {
            Id = "wizard-corrupt-identity",
            Url = context.GatewayUrl!,
            SharedGatewayToken = "shared-token",
            IsLocal = true,
        });
        registry.SetActive(record.Id);
        registry.Save();
        context.GatewayRecordId = record.Id;

        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(identityDirectory);
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        File.WriteAllText(identityPath, "{");
        var originalBytes = File.ReadAllBytes(identityPath);

        var result = await new SetupWizardRunner(context).RunAsync(CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, result.Message);
        Assert.IsType<DeviceIdentityLoadException>(result.Error);
        Assert.Equal(originalBytes, File.ReadAllBytes(identityPath));
        Assert.False(Directory.Exists(Path.Combine(identityDirectory, "setup-wizard")));
        Assert.Empty(Directory.GetFiles(identityDirectory, ".device-key-ed25519.json.*.tmp"));
    }

    [Fact]
    public void WindowsNodeContext_CanSkipWhenDisabled()
    {
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { Enabled = false }
        });

        Assert.True(new WindowsNodeBootstrapContextStep().CanSkip(ctx));
    }

    [Fact]
    public void WindowsNodeContext_BuildApplyScript_UsesAbsolutePathAndMinimalShape()
    {
        var script = WindowsNodeBootstrapContextStep.BuildApplyScript("/home/openclaw/.openclaw/workspace");

        Assert.Contains("set -o pipefail", script);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", script);
        Assert.Contains("AGENTS_SYMLINK:$agents", script);
        Assert.Contains("mkdir -p \"$workspace\"", script);
        Assert.Contains(": > \"$agents\"", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK", script);
        Assert.Contains("awk -v BEGIN_M=\"$begin_marker\" -v END_M=\"$end_marker\"", script);
        Assert.Contains("printf '%s' \"$block_b64\" | base64 -d >> \"$tmp\"", script);
        Assert.Contains("mktemp \"$workspace/.AGENTS.md.openclaw.XXXXXX\"", script);
        Assert.Contains("chmod --reference=\"$agents\" \"$tmp\"", script);
        Assert.Contains("sub(/\\r$/, \"\", marker_line)", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_READY", script);
        // Must not depend on node or carry an embedded JS payload.
        Assert.DoesNotContain(" node ", script);
        Assert.DoesNotContain(" node -", script);
        Assert.DoesNotContain("apply_js_b64", script);
        Assert.DoesNotContain("openclaw setup", script);
        Assert.DoesNotContain("openclaw config get", script);
        Assert.DoesNotContain("AGENTS_MISSING_AFTER_SETUP", script);
        Assert.DoesNotContain("$HOME", script);
        Assert.DoesNotContain("case \"$candidate\"", script);
        Assert.DoesNotContain("<<'NODE'", script);
        Assert.DoesNotContain("OPENCLAW_GATEWAY_TOKEN", script);
    }

    [Fact]
    public void WindowsNodeContext_BuildRollbackScript_UsesAbsolutePathAndMinimalShape()
    {
        var script = WindowsNodeBootstrapContextStep.BuildRollbackScript("/home/openclaw/.openclaw/workspace");

        Assert.Contains("set -o pipefail", script);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", script);
        Assert.Contains("awk -v BEGIN_M=\"$begin_marker\" -v END_M=\"$end_marker\"", script);
        Assert.Contains("mktemp \"$workspace/.AGENTS.md.openclaw.XXXXXX\"", script);
        Assert.Contains("chmod --reference=\"$agents\" \"$tmp\"", script);
        Assert.Contains("sub(/\\r$/, \"\", marker_line)", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_ABSENT", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", script);
        Assert.Contains("AGENTS_SYMLINK_ROLLBACK_SKIPPED:$agents", script);
        Assert.Contains("exit 5", script);
        // Must not depend on node or carry an embedded JS payload.
        Assert.DoesNotContain(" node ", script);
        Assert.DoesNotContain(" node -", script);
        Assert.DoesNotContain("rollback_js_b64", script);
        Assert.DoesNotContain("openclaw setup", script);
        Assert.DoesNotContain("openclaw config get", script);
        Assert.DoesNotContain("rm -f \"$agents\"", script);
        Assert.DoesNotContain("$HOME", script);
        Assert.DoesNotContain("case \"$candidate\"", script);
        Assert.DoesNotContain("<<'NODE'", script);
    }

    // Characterization (PR 3 WslShellQuoting migration): the workspace path is emitted as a
    // fully-wrapped POSIX token, so an embedded single quote must close-escape-reopen ('\'')
    // and remain single-quoted. Pins the generated script byte-for-byte.
    [Fact]
    public void WindowsNodeContext_BuildApplyScript_QuotesWorkspacePathWithEmbeddedSingleQuote()
    {
        var script = WindowsNodeBootstrapContextStep.BuildApplyScript("/home/o'brien/.openclaw/workspace");

        Assert.Contains("workspace='/home/o'\\''brien/.openclaw/workspace'", script);
    }

    [Fact]
    public void WindowsNodeContext_BuildRollbackScript_QuotesWorkspacePathWithEmbeddedSingleQuote()
    {
        var script = WindowsNodeBootstrapContextStep.BuildRollbackScript("/home/o'brien/.openclaw/workspace");

        Assert.Contains("workspace='/home/o'\\''brien/.openclaw/workspace'", script);
    }

    [Theory]
    [InlineData("/home/openclaw/.openclaw/workspace", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("~", "/home/openclaw", "/home/openclaw")]
    [InlineData("~/.openclaw/custom workspace", "/home/openclaw", "/home/openclaw/.openclaw/custom workspace")]
    [InlineData("relative/path", "/home/openclaw", "/home/openclaw/relative/path")]
    [InlineData("", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("null", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("undefined", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("/abs/path", "/home/openclaw/", "/abs/path")]
    [InlineData("~/x", "/home/openclaw/", "/home/openclaw/x")]
    public void WindowsNodeContext_ExpandLinuxPath_ResolvesCorrectly(string input, string home, string expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExpandLinuxPath(input, home));
    }

    [Theory]
    [InlineData("\"/home/openclaw/.openclaw/workspace\"\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("Config warnings:\n- plugins.entries.device-pair\n\"/home/openclaw/.openclaw/workspace\"\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("\"~/.openclaw/workspace\"\n", "~/.openclaw/workspace")]
    [InlineData("/home/openclaw/.openclaw/workspace\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("null\n", null)]
    [InlineData("", null)]
    public void WindowsNodeContext_ExtractWorkspaceFromConfigOutput_ParsesValues(string stdout, string? expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExtractWorkspaceFromConfigOutput(stdout));
    }

    [Theory]
    [InlineData("[{\"id\":\"main\",\"workspace\":\"/home/openclaw/main\",\"isDefault\":true}]", "/home/openclaw/main")]
    [InlineData("Warning\n[\n  {\"id\":\"other\",\"workspace\":\"/home/openclaw/other\",\"isDefault\":false},\n  {\"id\":\"primary\",\"workspace\":\"/home/openclaw/primary\",\"isDefault\":true}\n]\n", "/home/openclaw/primary")]
    [InlineData("[{\"id\":\"main\",\"workspace\":\"~/main\"}]", "~/main")]
    [InlineData("not json", null)]
    public void WindowsNodeContext_ExtractDefaultAgentWorkspace_ParsesCanonicalAgentsList(string stdout, string? expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExtractDefaultAgentWorkspaceFromAgentsOutput(stdout));
    }

    [Fact]
    public void WindowsNodeContext_IsMissingDistroResult_InspectsBothOutputStreams()
    {
        var result = new CommandResult(
            -1,
            "There is no distribution with the supplied name.",
            "wsl: warning: ignored setting",
            TimeSpan.Zero,
            TimedOut: false);

        Assert.True(WindowsNodeBootstrapContextStep.IsMissingDistroResult(result));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_RunsInWslAsConfiguredUserAndResolvesWorkspace()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("WINDOWS_NODE_CONTEXT_READY"))
                    return Ok(string.Join("\n",
                        "WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK:/home/openclaw/.openclaw/workspace/AGENTS.md",
                        "WINDOWS_NODE_CONTEXT_WORKSPACE:/home/openclaw/.openclaw/workspace",
                        "WINDOWS_NODE_CONTEXT_READY",
                        ""));
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(5, commands.WslCalls.Count);
        Assert.All(commands.WslCalls, c =>
        {
            Assert.Equal("OpenClawGateway", c.DistroName);
            Assert.Equal("openclaw", c.User);
        });
        Assert.Contains("getent passwd", commands.WslCalls[0].Command);
        Assert.Contains("openclaw agents list --json", commands.WslCalls[1].Command);
        Assert.Contains("openclaw config get agents.defaults.workspace", commands.WslCalls[2].Command);
        Assert.Contains("openclaw setup --help", commands.WslCalls[3].Command);
        Assert.Contains("openclaw setup --baseline --workspace '/home/openclaw/.openclaw/workspace'", commands.WslCalls[3].Command);
        Assert.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'", commands.WslCalls[3].Command);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", commands.WslCalls[4].Command);
        // getent uses $(id -un) command-substitution and no $vars, so argv path is safe.
        Assert.False(commands.WslCalls[0].InputViaStdin);
        // agents list + config get + openclaw setup scripts reference $PATH via WslPathPrefix,
        // which wsl.exe would rewrite on the argv path — see docs/WSL_EXE_ARGV_PITFALL.md.
        Assert.True(commands.WslCalls[1].InputViaStdin);
        Assert.True(commands.WslCalls[2].InputViaStdin);
        Assert.True(commands.WslCalls[3].InputViaStdin);
        // Apply script uses $workspace etc., must use stdin.
        Assert.True(commands.WslCalls[4].InputViaStdin);
        var state = await WindowsNodeBootstrapContextStep.ReadInstallStateAsync(ctx, CancellationToken.None);
        Assert.Contains(state.Targets, target =>
            target.DistroName == "OpenClawGateway" &&
            target.User == "openclaw" &&
            target.WorkspacePath == "/home/openclaw/.openclaw/workspace");
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_ResolvesRelativeConfiguredWorkspaceFromGatewayUserHome()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/relative/workspace"));
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"relative/workspace\"\n");
                if (command.Contains("workspace='/home/openclaw/relative/workspace'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/relative/workspace'"));
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("workspace='/home/openclaw/relative/workspace'"));
        Assert.DoesNotContain(commands.WslCalls, c => c.Command == "pwd -P");
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesDefaultOnlyWhenWorkspaceKeyIsAbsent()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return new CommandResult(
                        1,
                        "",
                        "Config path not found: agents.defaults.workspace. Run openclaw config validate to inspect config shape.",
                        TimeSpan.Zero,
                        TimedOut: false);
                if (command.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'"))
                    return Ok();
                if (command.Contains("workspace='/home/openclaw/.openclaw/workspace'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_DoesNotPersistDefaultWhenWorkspaceLookupFails()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Fail("gateway config is temporarily unavailable");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve OpenClaw default workspace path", result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_DoesNotPersistDefaultForMalformedWorkspaceOutput()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("Config warning without a value\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesEffectiveDefaultAgentWorkspaceWithoutRewritingDefaults()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/main-agent"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("workspace='/home/openclaw/main-agent'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
        Assert.Contains(commands.WslCalls, c => c.Command.Contains("workspace='/home/openclaw/main-agent'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWhenEffectiveAgentWorkspaceLookupFails()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Fail("agents unavailable");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        var priorTarget = new WindowsNodeContextTarget("prior-distro", "openclaw", "/prior/workspace");
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            priorTarget,
            CancellationToken.None);

        var step = new WindowsNodeBootstrapContextStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);
        var callsAfterExecute = commands.WslCalls.Count;
        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve OpenClaw agent workspace path", result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw config get"));
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
        Assert.Equal(callsAfterExecute, commands.WslCalls.Count);
        var state = await WindowsNodeBootstrapContextStep.ReadInstallStateAsync(ctx, CancellationToken.None);
        Assert.Equal([priorTarget], state.Targets);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_RemovesNewStateWhenSymlinkCheckMakesNoChange()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("openclaw setup"))
                    return Ok();
                if (command.Contains("AGENTS_SYMLINK:$agents"))
                    return new CommandResult(2, "", "AGENTS_SYMLINK", TimeSpan.Zero, TimedOut: false);
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var step = new WindowsNodeBootstrapContextStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);
        var callsAfterExecute = commands.WslCalls.Count;
        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
        Assert.Equal(callsAfterExecute, commands.WslCalls.Count);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesExplicitWorkspaceOverride()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup --workspace"))
                    return Ok("");
                if (command.Contains("workspace='/custom/abs/path'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { WorkspacePath = "/custom/abs/path" }
        }, commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        // No config-get call when override is set.
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw config get"));
        // Absolute path threads through to BOTH the setup command and the apply script
        // (verified by the apply script asserting workspace='/custom/abs/path').
        Assert.Contains(commands.WslCalls, c => c.Command.Contains("openclaw setup --workspace '/custom/abs/path'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_OverrideWithTilde_ExpandsBeforePassingToSetup()
    {
        // Regression: a ~/foo override must be expanded once so that the same
        // absolute path goes to `openclaw setup --workspace` and the apply script.
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup --workspace"))
                    return Ok("");
                if (command.Contains("workspace='/home/openclaw/custom-ws'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { WorkspacePath = "~/custom-ws" }
        }, commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/custom-ws'"));
        Assert.DoesNotContain(commands.WslCalls,
            c => c.Command.Contains("--workspace '~/custom-ws'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_RunsRollbackScriptViaStdin()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("recorded-distro", "recorded-user", "/recorded/workspace"),
            CancellationToken.None);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.NotEmpty(commands.WslCalls);
        // Last call is the rollback script and must use stdin.
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", commands.WslCalls[^1].Command);
        Assert.True(commands.WslCalls[^1].InputViaStdin);
        var rollback = Assert.Single(commands.WslCalls);
        Assert.Equal("recorded-distro", rollback.DistroName);
        Assert.Equal("recorded-user", rollback.User);
        Assert.Contains("workspace='/recorded/workspace'", rollback.Command);
        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_PropagatesCleanupFailureAndKeepsStateForRetry()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) => command.Contains("WINDOWS_NODE_CONTEXT_REMOVED")
                ? Fail("cannot update AGENTS.md")
                : Fail($"unexpected wsl command: {command}"));
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("recorded-distro", "recorded-user", "/recorded/workspace"),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains("cannot update AGENTS.md", error.Message);
        Assert.True(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_RemovesExistingTargetStateAfterCleanup()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("openclaw setup"))
                    return Ok();
                if (command.Contains("WINDOWS_NODE_CONTEXT_READY"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        var target = new WindowsNodeContextTarget(
            "OpenClawGateway",
            "openclaw",
            "/home/openclaw/.openclaw/workspace");
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(ctx, target, CancellationToken.None);
        var step = new WindowsNodeBootstrapContextStep();
        Assert.True((await step.ExecuteAsync(ctx, CancellationToken.None)).IsSuccess);

        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_TreatsMissingRecordedDistroAsCleaned()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) => command.Contains("WINDOWS_NODE_CONTEXT_REMOVED")
                ? new CommandResult(1, "", "WSL_E_DISTRO_NOT_FOUND", TimeSpan.Zero, TimedOut: false)
                : Fail($"unexpected wsl command: {command}"));
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("missing-distro", "openclaw", "/recorded/workspace"),
            CancellationToken.None);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_SkipsLegacyCleanupWhenDistroIsAbsent()
    {
        var commands = new FakeCommandRunner(
            arguments =>
            {
                Assert.Equal(["--list", "--quiet"], arguments);
                return Ok("Ubuntu\n");
            });
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.Empty(commands.WslCalls);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_SkipsLegacyCleanupWhenWslHasNoDistributions()
    {
        var commands = new FakeCommandRunner(
            arguments =>
            {
                Assert.Equal(["--list", "--quiet"], arguments);
                return new CommandResult(
                    1,
                    "",
                    "Windows Subsystem for Linux has no installed distributions.\n" +
                    "Use 'wsl.exe --list --online' to list available distributions and " +
                    "'wsl.exe --install <Distro>' to install.",
                    TimeSpan.Zero,
                    TimedOut: false);
            });
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.Empty(commands.WslCalls);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_SkipsLegacyCleanupWhenWslExeCannotStart()
    {
        var commands = new FakeCommandRunner(
            _ => new CommandResult(
                -1,
                "",
                @"Failed to start process 'C:\Windows\System32\wsl.exe': The system cannot find the file specified.",
                TimeSpan.Zero,
                TimedOut: false));
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.Empty(commands.WslCalls);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_FailsWhenDistroInspectionIsAmbiguous()
    {
        var commands = new FakeCommandRunner(_ => Fail("Access is denied."));
        var ctx = CreateContext(commands: commands);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains(
            "Could not inspect WSL distributions while cleaning legacy Windows node context",
            error.Message);
        Assert.Empty(commands.WslCalls);
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_FailsWhenDistroInspectionTimesOut()
    {
        var commands = new FakeCommandRunner(_ => TimedOut());
        var ctx = CreateContext(commands: commands);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains(
            "Could not inspect WSL distributions while cleaning legacy Windows node context",
            error.Message);
        Assert.Empty(commands.WslCalls);
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_CleansLegacyEffectiveWorkspaceWithoutStateFile()
    {
        var commands = new FakeCommandRunner(
            arguments =>
            {
                Assert.Equal(["--list", "--quiet"], arguments);
                return Ok("OpenClawGateway\n");
            },
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/legacy-main"));
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.Contains(commands.WslCalls, call => call.Command.Contains("getent passwd"));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("openclaw agents list --json"));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("workspace='/home/openclaw/legacy-main'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWhenHomeUnresolvable()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return new CommandResult(1, "", "", TimeSpan.Zero, TimedOut: false);
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve Linux home directory", result.Message);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWithoutReadyMarker()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                return Fail("apply script failed");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Windows node context injection failed", result.Message);
    }

    private static CommandResult Ok(string stdout = "", string stderr = "")
        => new(0, stdout, stderr, TimeSpan.Zero, TimedOut: false);

    private static CommandResult Fail(string stderr = "")
        => new(1, "", stderr, TimeSpan.Zero, TimedOut: false);

    private static CommandResult FailWithStdout(string stdout)
        => new(1, stdout, "", TimeSpan.Zero, TimedOut: false);

    private static CommandResult TimedOut()
        => new(-1, "", "", TimeSpan.FromSeconds(30), TimedOut: true);

    private static FakeCommandRunner CreateReloadRestorationRunner() =>
        new(
            _ => Ok(),
            (_, command, _) => command switch
            {
                var value when value.Contains("config set gateway.reload.mode 'hybrid'") => Ok(),
                var value when value.Contains("openclaw gateway restart") => Ok(),
                var value when value.Contains("curl -s") => Ok("200"),
                _ => Fail($"Unexpected command: {command}"),
            });

    private static void AssertReloadRestorationCompleted(
        FakeCommandRunner commands)
    {
        Assert.Contains(
            commands.WslCalls,
            call => call.Command.Contains("config set gateway.reload.mode 'hybrid'"));
        Assert.Contains(
            commands.WslCalls,
            call => call.Command.Contains("openclaw gateway restart"));
        Assert.Contains(
            commands.WslCalls,
            call => call.Command.Contains("curl -s"));
    }

    private static void TrustManagedEndpoint(SetupContext ctx)
    {
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                ctx.Config.GatewayPort));
    }

    private static string AgentsListJson(string workspace, string id = "main", bool isDefault = true)
        => JsonSerializer.Serialize(new[] { new { id, workspace, isDefault } });

    private static string NulSeparated(string value)
        => string.Join("\0", value.ToCharArray()) + "\0";

    private SetupContext CreatePairingContext(string failureStdout)
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => FailWithStdout(failureStdout));
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        ctx.SharedGatewayToken = "shared-token";
        return ctx;
    }

    // Shared scenario for the node-pairing cancellation tests: a reachable gateway HTTP endpoint (so
    // WindowsGatewayReachability.VerifyAsync passes), a silent WebSocket the node client parks against,
    // a fake WSL runner whose approval drain is empty, and a seeded gateway registry record — enough to
    // drive the REAL PairNodeStep to its node-connection wait. `ctxToken` becomes the SetupContext's
    // CancellationToken (used by the pipeline); pass CancellationToken.None when cancelling the step call
    // directly. The caller disposes the returned `ws`/`http`.
    private (SetupContext ctx, SilentWebSocketServer ws, HttpListener http, SetupLogger logger)
        BuildNodePairingScenario(CancellationToken ctxToken)
    {
        var httpPort = GetFreeTcpPort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{httpPort}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = await http.GetContextAsync(); }
                catch { return; }
                c.Response.StatusCode = 200;
                c.Response.Close();
            }
        });

        var ws = new SilentWebSocketServer();
        var commands = new FakeCommandRunner(_ => Ok(), (_, _, _) => Ok(stdout: "No pending device approvals"));
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = new SetupContext(
            new SetupConfig { GatewayPort = httpPort }, logger,
            new TransactionJournal(filePath: null), commands, ctxToken);
        ctx.DistroName = "test-distro";
        ctx.SharedGatewayToken = "test-token-placeholder";
        ctx.GatewayUrl = $"ws://127.0.0.1:{ws.Port}";
        ctx.GatewayRecordId = "test-gw";

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "test-gw",
            Url = ctx.GatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
            SshTunnel = null,
        });
        registry.Save();
        return (ctx, ws, http, logger);
    }

    // The step itself must rethrow a caller cancel rather than swallow it into StepResult.Fail.
    [Fact]
    public async Task PairNodeStep_CallerCancellation_PropagatesInsteadOfFailing()
    {
        var (ctx, ws, http, _) = BuildNodePairingScenario(CancellationToken.None);
        using (ws)
        using (http)
        {
            using var callerCts = new CancellationTokenSource();
            var task = new PairNodeStep().ExecuteAsync(ctx, callerCts.Token);
            await ws.UpgradeCompleted;   // deterministic barrier: the client is in its node-connection wait
            callerCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    // Durable end-to-end contract (the user-visible behavior the PR promises): driving the REAL
    // SetupPipeline with the real PairNodeStep and a caller cancel, the pipeline reports Cancelled/exit 3,
    // emits NO "connection failed"/retry warning, and journals the abort as a cancellation — not a
    // failed-step narrative. On base the step's retry masks the *outcome* to Cancelled too, so the
    // log/journal assertions are what actually pin this fix.
    [Fact]
    public async Task SetupPipeline_CallerCancel_CancelsCleanlyWithoutFailureNarrative()
    {
        using var callerCts = new CancellationTokenSource();
        var (ctx, ws, http, logger) = BuildNodePairingScenario(callerCts.Token);
        using (ws)
        using (http)
        {
            var logs = new List<LogEntry>();
            logger.LogEmitted += (_, e) => { lock (logs) { logs.Add(e); } };

            var pipeline = new SetupPipeline(new SetupStep[] { new PairNodeStep() }, rollbackOnFailureOverride: false);
            var run = pipeline.RunAsync(ctx);
            await ws.UpgradeCompleted;
            callerCts.Cancel();
            var result = await run;

            _output.WriteLine($"SetupPipeline on caller-cancel → Outcome={result.Outcome}, ExitCode={result.ExitCode}");

            // 1. user-observable outcome
            Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
            Assert.Equal(3, result.ExitCode);

            // 2. no misleading connection-failure / retry warning for a user abort
            List<LogEntry> snapshot;
            lock (logs) { snapshot = logs.ToList(); }
            Assert.DoesNotContain(snapshot, e => e.Message.Contains("Node connection failed", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot, e => e.Level == LogLevel.Warn && e.Message.Contains("retrying", StringComparison.OrdinalIgnoreCase));

            // 3. journal records the cancellation, not a failed-step narrative
            Assert.Contains(ctx.Journal.Entries, en => en.Event == "pipeline_cancelled");
            Assert.DoesNotContain(ctx.Journal.Entries, en => en.Event == "pipeline_failed");
        }
    }

    [Fact]
    public async Task PreflightWindowsTailscale_RequiresRunningMagicDnsClientBeforeCleanup()
    {
        var config = new SetupConfig { Tailscale = new TailscaleConfig { Enabled = true } };
        var commands = new FakeCommandRunner(_ => Ok("""{"BackendState":"Running","Self":{"DNSName":"windows.tailnet.ts.net"}}"""));
        var ctx = CreateContext(config, commands);

        var result = await new PreflightWindowsTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("tailnet.ts.net", ctx.WindowsTailnetDnsSuffix);
        Assert.Equal("tailnet.ts.net", config.Tailscale.TailnetDnsSuffix);
        Assert.Contains(commands.Calls, call => call.Arguments.SequenceEqual(["status", "--json"]));
    }

    [Fact]
    public async Task InstallTailscale_UsesSignedUbuntuNoblePackageRepository()
    {
        var config = new SetupConfig { Tailscale = new TailscaleConfig { Enabled = true } };
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, _, _) => Ok("tailscale 1.98.8"));
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";

        var result = await new InstallTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var install = Assert.Single(commands.WslCalls);
        Assert.Equal("root", install.User);
        Assert.True(install.InputViaStdin);
        Assert.Contains("VERSION_ID\" != \"24.04", install.Command);
        Assert.Contains("noble.noarmor.gpg", install.Command);
        Assert.Contains("signed-by=/usr/share/keyrings/tailscale-archive-keyring.gpg", install.Command);
        Assert.Contains("apt-get install -y tailscale", install.Command);
        Assert.DoesNotContain("tailscale.com/install.sh", install.Command);
        Assert.DoesNotContain("gpg --show-keys", install.Command);
        Assert.DoesNotContain("dpkg-statoverride", install.Command);
    }

    [Fact]
    public async Task PreflightWindowsTailscale_RejectsUnsupportedBaseDistroBeforeRunningCommands()
    {
        var config = new SetupConfig
        {
            BaseDistro = "Debian",
            Tailscale = new TailscaleConfig { Enabled = true }
        };
        var commands = new FakeCommandRunner(_ => Fail("Windows commands are not expected"));
        var ctx = CreateContext(config, commands);

        var result = await new PreflightWindowsTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Ubuntu-24.04", result.Message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task AuthorizeTailscale_AuthKeyUsesTransientEnvironmentAndDerivesMagicDnsName()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig
            {
                Enabled = true,
                AuthMode = TailscaleAuthMode.AuthKey,
                AuthKey = "tskey-auth-only-in-memory",
                AuthTimeoutSeconds = 30,
            }
        };
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("tailscale status --json")
                ? Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                : Ok());
        var ctx = CreateContext(config, commands);
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";

        var result = await new AuthorizeTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("openclaw.tailnet.ts.net", ctx.TailscaleDnsName);
        Assert.Null(config.Tailscale.AuthKey);
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("--auth-key=\"$TS_AUTHKEY\""));
        Assert.Contains(commands.WslEnvironments, environment => environment?["TS_AUTHKEY"] == "tskey-auth-only-in-memory");
        Assert.DoesNotContain(commands.WslCalls, call => call.Command.Contains("--operator=", StringComparison.Ordinal));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale up", StringComparison.Ordinal) && call.User == "root");
    }

    [Fact]
    public void AuthorizeTailscale_DoesNotUsePipelineRetriesForOneShotAuthorization()
    {
        var step = new AuthorizeTailscaleStep();

        Assert.False(step.CanRetry);
        Assert.Equal(1, step.Retry.MaxAttempts);
    }

    [Fact]
    public async Task AuthorizeTailscale_BrowserPresentsUrlWithoutWritingItToSetupState()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthTimeoutSeconds = 30 }
        };
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("tailscale up")
                ? FailWithStdout("https://login.tailscale.com/a/browser-only-token")
                : command.Contains("tailscale status --json")
                    ? Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                    : Ok());
        var presenter = new RecordingAuthorizationPresenter();
        var ctx = CreateContext(config, commands);
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new AuthorizeTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Tailscale", presenter.Request!.Provider);
        Assert.Equal("https://login.tailscale.com/a/browser-only-token", presenter.Request.AuthorizationUri.AbsoluteUri);
    }

    [Fact]
    public async Task AuthorizeTailscale_BrowserReissuesOneStaleAuthorizationPath()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthTimeoutSeconds = 30 }
        };
        var upCalls = 0;
        var statusCalls = 0;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale up", StringComparison.Ordinal)
                ? ++upCalls == 1
                    ? FailWithStdout("https://login.tailscale.com/a/first-token")
                    : FailWithStdout("https://login.tailscale.com/a/second-token")
                : command.Contains("tailscale status --json", StringComparison.Ordinal)
                    ? ++statusCalls == 1
                        ? Ok("""{"BackendState":"NeedsLogin","Health":["register request: http 410: auth path not found"]}""")
                        : Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                    : Ok());
        var presenter = new RecordingAuthorizationPresenter();
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new AuthorizeTailscaleStep(new AdvancingTailscaleClock()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, presenter.Requests.Count);
        Assert.Equal("https://login.tailscale.com/a/second-token", presenter.Request!.AuthorizationUri.AbsoluteUri);
        Assert.Equal(2, upCalls);
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale up", StringComparison.Ordinal) && call.Command.Contains("--force-reauth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorizeTailscale_BrowserPresentsOneReplacementStatusUrlWithoutReissuingUp()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthTimeoutSeconds = 30 }
        };
        var upCalls = 0;
        var statusCalls = 0;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale up", StringComparison.Ordinal)
                ? ++upCalls == 1
                    ? FailWithStdout("https://login.tailscale.com/a/first-token")
                    : Fail("a replacement status URL must not rerun tailscale up")
                : command.Contains("tailscale status --json", StringComparison.Ordinal)
                    ? ++statusCalls == 1
                        ? Ok("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/replacement-token"}""")
                        : Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                    : Ok());
        var presenter = new RecordingAuthorizationPresenter();
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new AuthorizeTailscaleStep(new AdvancingTailscaleClock()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, upCalls);
        Assert.Equal(2, presenter.Requests.Count);
        Assert.Equal("https://login.tailscale.com/a/replacement-token", presenter.Request!.AuthorizationUri.AbsoluteUri);
    }

    [Fact]
    public async Task AuthorizeTailscale_BrowserReadsAuthorizationUrlFromStatusWhenUpDoesNotPrintIt()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthTimeoutSeconds = 30 }
        };
        var statusCalls = 0;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale up", StringComparison.Ordinal)
                ? Fail()
                : command.Contains("tailscale status --json", StringComparison.Ordinal)
                    ? ++statusCalls == 1
                        ? Ok("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/status-only-token"}""")
                        : Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                    : Ok());
        var presenter = new RecordingAuthorizationPresenter();
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new AuthorizeTailscaleStep(new AdvancingTailscaleClock()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("https://login.tailscale.com/a/status-only-token", presenter.Request!.AuthorizationUri.AbsoluteUri);
        Assert.Equal(2, statusCalls);
    }

    [Fact]
    public async Task FinalizeTailscaleServe_RootOwnsServeAndPublishesOnlyWssAfterHealthCheck()
    {
        const string expectedStatus = """
            { "Web": { "openclaw.tailnet.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } } } } }
            """;
        var routeConfigured = false;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) =>
            {
                if (command.Contains("tailscale serve status --json", StringComparison.Ordinal))
                    return routeConfigured ? Ok(expectedStatus) : Ok("{}");
                if (command.Contains("tailscale serve --bg --yes", StringComparison.Ordinal))
                {
                    routeConfigured = true;
                    return Ok();
                }
                if (command.Contains("plugins.entries.device-pair.config.publicUrl", StringComparison.Ordinal))
                    return Ok();
                return Fail($"unexpected wsl command: {command}");
            });
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, ServeApprovalTimeoutSeconds = 30 }
        };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.TailscaleDnsName = "openclaw.tailnet.ts.net";
        var probe = new FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult.Reachable(401));

        var result = await new FinalizeTailscaleServeStep(new AdvancingTailscaleClock(), probe)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("wss://openclaw.tailnet.ts.net", ctx.GatewayUrl);
        Assert.Equal("https://openclaw.tailnet.ts.net", probe.Endpoint!.AbsoluteUri.TrimEnd('/'));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale serve --bg --yes", StringComparison.Ordinal) && call.User == "root");
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("device-pair.config.publicUrl", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.WslCalls, call => call.Command.Contains("tailscale serve", StringComparison.Ordinal) && call.User == "openclaw");
    }

    [Fact]
    public async Task FinalizeTailscaleServe_PollsUntilBrowserApprovalConfiguresRoute()
    {
        const string expectedStatus = """
            { "Web": { "openclaw.tailnet.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } } } } }
            """;
        var statusCalls = 0;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale serve status --json", StringComparison.Ordinal)
                ? ++statusCalls == 1 ? Ok("{}") : Ok(expectedStatus)
                : command.Contains("tailscale serve --bg --yes", StringComparison.Ordinal)
                    ? RequestBrowserApproval()
                    : command.Contains("plugins.entries.device-pair.config.publicUrl", StringComparison.Ordinal)
                        ? Ok()
                        : Fail($"unexpected wsl command: {command}"));
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, ServeApprovalTimeoutSeconds = 30 }
        };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.TailscaleDnsName = "openclaw.tailnet.ts.net";
        var presenter = new RecordingAuthorizationPresenter();
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new FinalizeTailscaleServeStep(
                new AdvancingTailscaleClock(),
                new FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult.Reachable(401)))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, statusCalls);
        Assert.Equal("https://login.tailscale.com/a/serve-approval", presenter.Request!.AuthorizationUri.AbsoluteUri);
        Assert.Equal("wss://openclaw.tailnet.ts.net", ctx.GatewayUrl);

        CommandResult RequestBrowserApproval()
        {
            return FailWithStdout("https://login.tailscale.com/a/serve-approval");
        }
    }

    [Fact]
    public async Task TailscaleHealthFailure_RollsBackServeAndDaemonBeforePairing()
    {
        const string expectedStatus = """
            { "Web": { "openclaw.tailnet.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } } } } }
            """;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale serve status --json", StringComparison.Ordinal)
                ? Ok(expectedStatus)
                : Ok());
        var config = new SetupConfig
        {
            RollbackOnFailure = true,
            Tailscale = new TailscaleConfig { Enabled = true }
        };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.TailscaleDnsName = "openclaw.tailnet.ts.net";
        var pipeline = new SetupPipeline([
            new InstallTailscaleStep(),
            new FinalizeTailscaleServeStep(
                new AdvancingTailscaleClock(),
                new FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult.Unreachable("connection refused")))
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("finalize-tailscale-serve", result.FailedStepId);
        Assert.Contains("could not reach", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("wss://openclaw.tailnet.ts.net", ctx.GatewayUrl);
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale funnel reset", StringComparison.Ordinal) && call.User == "root");
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale serve reset", StringComparison.Ordinal) && call.User == "root");
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale logout", StringComparison.Ordinal) && call.Command.Contains("disable --now tailscaled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizeTailscaleServe_PollsBrowserApprovalWithoutFixedDelayAndTimesOutBoundedly()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale serve status --json", StringComparison.Ordinal)
                ? Ok("{}")
                : command.Contains("tailscale serve --bg --yes", StringComparison.Ordinal)
                    ? FailWithStdout("https://login.tailscale.com/a/serve-approval")
                    : Fail($"unexpected wsl command: {command}"));
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, ServeApprovalTimeoutSeconds = 30 }
        };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.TailscaleDnsName = "openclaw.tailnet.ts.net";
        var presenter = new RecordingAuthorizationPresenter();
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new FinalizeTailscaleServeStep(new AdvancingTailscaleClock(), new FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult.Reachable(200)))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("within 30 seconds", result.Message);
        Assert.NotNull(presenter.Request);
        Assert.Equal("https://login.tailscale.com/a/serve-approval", presenter.Request!.AuthorizationUri.AbsoluteUri);
        Assert.True(commands.WslCalls.Count(call => call.Command.Contains("tailscale serve --bg --yes", StringComparison.Ordinal)) > 1);
    }

    [Fact]
    public async Task FinalizeTailscaleServe_RejectsFunnelAndRollbackResetsServeBeforeDistroCleanup()
    {
        const string funnelStatus = """
            {
              "AllowFunnel": { "openclaw.tailnet.ts.net:443": true },
              "Web": { "openclaw.tailnet.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } } } }
            }
            """;
        var commands = new FakeCommandRunner(
            _ => Fail("Windows commands are not expected"),
            (_, command, _) => command.Contains("tailscale serve status --json", StringComparison.Ordinal)
                ? Ok(funnelStatus)
                : Ok());
        var config = new SetupConfig { Tailscale = new TailscaleConfig { Enabled = true } };
        var ctx = CreateContext(config, commands);
        ctx.DistroName = "test-distro";
        ctx.TailscaleDnsName = "openclaw.tailnet.ts.net";
        var step = new FinalizeTailscaleServeStep(new AdvancingTailscaleClock(), new FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult.Reachable(200)));

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);
        await step.RollbackAsync(ctx, CancellationToken.None);
        await new InstallTailscaleStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Funnel is configured", result.Message);
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale funnel reset", StringComparison.Ordinal) && call.User == "root");
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale serve reset", StringComparison.Ordinal) && call.User == "root");
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("tailscale logout", StringComparison.Ordinal) && call.Command.Contains("disable --now tailscaled", StringComparison.Ordinal));
    }

    private sealed class FakeWslRegistrationInspector(
        WslRegistrationInspection inspection) : IWslRegistrationInspector
    {
        public static FakeWslRegistrationInspector Found(string basePath) =>
            new(new WslRegistrationInspection(
                WslRegistrationInspectionStatus.Found,
                Guid.NewGuid().ToString("B"),
                basePath));

        public WslRegistrationInspection Inspect(string distroName) => inspection;
    }

    private sealed class FakeWslRegistrationSource(
        WslRegistrationSnapshot snapshot) : IWslRegistrationSource
    {
        public WslRegistrationSnapshot ReadAll() => snapshot;
    }

    private sealed class FakeCommandRunner(
        Func<string[], CommandResult> run,
        Func<string, string, TimeSpan, CommandResult>? runInWsl = null,
        Func<string, string[], TimeSpan, CancellationToken, CommandResult>? runWithCancellation = null) : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public List<(string Executable, string[] Arguments, TimeSpan Timeout)> TimedCalls { get; } = [];
        public List<(string Executable, string[] Arguments, string? StdinInput)> DetailedCalls { get; } = [];
        public List<(string DistroName, string Command, TimeSpan Timeout, string? User, bool InputViaStdin, CancellationToken CancellationToken)> WslCalls { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> WslEnvironments { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null)
        {
            Calls.Add((executable, arguments));
            TimedCalls.Add((executable, arguments, timeout));
            DetailedCalls.Add((executable, arguments, stdinInput));
            return Task.FromResult(runWithCancellation?.Invoke(executable, arguments, timeout, ct) ?? run(arguments));
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
            WslCalls.Add((distroName, command, timeout, user, inputViaStdin, ct));
            WslEnvironments.Add(environment);
            if (runInWsl == null)
                throw new NotSupportedException("RunInWslAsync is not expected in these tests.");

            return Task.FromResult(runInWsl(distroName, command, timeout));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public TimeSpan Elapsed => TimeSpan.FromTicks(_timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class RecordingAuthorizationPresenter : IExternalAuthorizationPresenter
    {
        public ExternalAuthorizationRequest? Request { get; private set; }
        public List<ExternalAuthorizationRequest> Requests { get; } = [];

        public Task PresentAsync(ExternalAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class AdvancingTailscaleClock : ITailscalePollingClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow = UtcNow.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTailscaleEndpointProbe(TailscaleEndpointProbeResult result) : ITailscaleEndpointProbe
    {
        public Uri? Endpoint { get; private set; }

        public Task<TailscaleEndpointProbeResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            return Task.FromResult(result);
        }
    }
}

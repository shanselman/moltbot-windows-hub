using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Concrete test double for WebSocketClientBase. 
/// Exposes hooks and tracking for unit testing base class behavior.
/// </summary>
public class TestWebSocketClient : WebSocketClientBase
{
    public List<string> ProcessedMessages { get; } = new();
    public int OnConnectedCallCount { get; private set; }
    public int OnDisconnectedCallCount { get; private set; }
    public int OnErrorCallCount { get; private set; }
    public Exception? LastError { get; private set; }
    public int OnDisposingCallCount { get; private set; }
    public bool AutoReconnectEnabled { get; set; } = true;
    public TimeSpan? TestConnectAttemptTimeout { get; set; }

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "test";
    protected override TimeSpan ConnectAttemptTimeout =>
        TestConnectAttemptTimeout ?? base.ConnectAttemptTimeout;

    public TestWebSocketClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger) { }

    protected override Task ProcessMessageAsync(string json)
    {
        ProcessedMessages.Add(json);
        return Task.CompletedTask;
    }

    protected override Task OnConnectedAsync()
    {
        OnConnectedCallCount++;
        return Task.CompletedTask;
    }

    protected override void OnDisconnected()
    {
        OnDisconnectedCallCount++;
    }

    protected override void OnError(Exception ex)
    {
        OnErrorCallCount++;
        LastError = ex;
    }

    protected override void OnDisposing()
    {
        OnDisposingCallCount++;
    }

    protected override bool ShouldAutoReconnect() => AutoReconnectEnabled;

    // Expose protected members for testing
    public void TestRaiseStatusChanged(ConnectionStatus status)
        => RaiseStatusChanged(status);

    public bool TestIsDisposed => IsDisposed;
    public bool TestIsConnected => IsConnected;
    public string TestGatewayUrlForDisplay => GatewayUrlForDisplay;
    public string TestToken => _token;
    public IOpenClawLogger TestLogger => _logger;
    public Task TestReconnectWithBackoffAsync() => ReconnectWithBackoffAsync();
    public void TestAbortCurrentWebSocket() => AbortCurrentWebSocket(CurrentConnectionGeneration);
}

[Collection("WebSocketClientBase")]
public class WebSocketClientBaseTests
{
    private readonly TestLogger _logger = new();

    [Fact]
    public void HandshakeChallengeGate_StaleGenerationCannotReplaceCurrentState()
    {
        var gate = new HandshakeChallengeGate();
        gate.Reset(2);

        Assert.True(gate.TryBegin(2));
        Assert.False(gate.TryBegin(1));
        Assert.True(gate.TryAuthorize(2));
        Assert.True(gate.IsAuthorized(2));
    }

    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://gateway.example.com", "wss://gateway.example.com")]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    [InlineData("wss://gateway.example.com", "wss://gateway.example.com")]
    public void Constructor_NormalizesUrl(string input, string expected)
    {
        var client = new TestWebSocketClient(input, "test-token", _logger);
        Assert.Equal(expected, client.TestGatewayUrlForDisplay);
        Assert.DoesNotContain("@", client.TestGatewayUrlForDisplay);
        client.Dispose();
    }

    [Fact]
    public void Constructor_StoresToken()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "my-token", _logger);
        Assert.Equal("my-token", client.TestToken);
        client.Dispose();
    }

    [Fact]
    public async Task AutoReconnect_AuthorizationDenied_DoesNotOpenSocket()
    {
        using var client = new TestWebSocketClient("ws://127.0.0.1:1", "strong-token", _logger);
        var authorizationCalls = 0;
        client.ReconnectAuthorizationAsync = _ =>
        {
            authorizationCalls++;
            return Task.FromResult(new ReconnectAuthorizationResult(
                false,
                GatewayErrorKind.LocalPortConflict,
                "blocked"));
        };

        await client.TestReconnectWithBackoffAsync();

        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, client.OnConnectedCallCount);
    }

    [Fact]
    public async Task AbortCurrentWebSocket_PreventsLaterMessagesOnAcceptedSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new TestWebSocketClient(server.WebSocketUrl, "strong-token", _logger)
        {
            AutoReconnectEnabled = false,
        };
        await client.ConnectAsync();
        await WaitForConditionAsync(
            () => server.AcceptedCount == 1,
            TimeSpan.FromSeconds(2));

        client.TestAbortCurrentWebSocket();
        try
        {
            await server.SendTextAsync("stale-socket-message");
        }
        catch (WebSocketException)
        {
            // The remote endpoint may observe the abort before attempting its send.
        }
        await Task.Delay(100);

        Assert.DoesNotContain("stale-socket-message", client.ProcessedMessages);
    }

    [Fact]
    public void Constructor_UsesNullLoggerWhenNotProvided()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token");
        Assert.NotNull(client.TestLogger);
        client.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsOnNullUrl()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient(null!, "token", _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyUrl()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("", "token", _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnNullToken()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("ws://localhost", null!, _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyToken()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("ws://localhost", "", _logger));
    }

    [Fact]
    public void Constructor_WithCredentialUrl_StripsFromDisplay()
    {
        var client = new TestWebSocketClient("ws://user:pass@localhost:18789", "token", _logger);
        Assert.Equal("ws://localhost:18789", client.TestGatewayUrlForDisplay);
        Assert.DoesNotContain("pass", client.TestGatewayUrlForDisplay);
        client.Dispose();
    }

    [Fact]
    public void Dispose_SetsIsDisposed()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        Assert.False(client.TestIsDisposed);
        client.Dispose();
        Assert.True(client.TestIsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        var disposedEvents = 0;
        client.Disposed += (_, _) => disposedEvents++;
        client.Dispose();
        client.Dispose(); // second call should not throw
        Assert.True(client.TestIsDisposed);
        Assert.Equal(1, disposedEvents);
        Assert.Equal(1, client.OnDisposingCallCount); // hook called only once
    }

    [Fact]
    public void Dispose_CallsOnDisposingHook()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.Dispose();
        Assert.Equal(1, client.OnDisposingCallCount);
    }

    [Fact]
    public void RaiseStatusChanged_FiresEvent()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        ConnectionStatus? received = null;
        client.StatusChanged += (_, status) => received = status;

        client.TestRaiseStatusChanged(ConnectionStatus.Connecting);

        Assert.Equal(ConnectionStatus.Connecting, received);
        client.Dispose();
    }

    [Fact]
    public void RaiseStatusChanged_WithNoSubscribers_DoesNotThrow()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.TestRaiseStatusChanged(ConnectionStatus.Connected); // no subscribers — should not throw
        client.Dispose();
    }

    [Fact]
    public void RaiseStatusChanged_MultipleSubscribers_AllNotified()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);
        client.StatusChanged += (_, s) => statuses.Add(s);

        client.TestRaiseStatusChanged(ConnectionStatus.Error);

        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(ConnectionStatus.Error, s));
        client.Dispose();
    }

    [Fact]
    public void IsConnected_FalseBeforeConnect()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        // Reflection to check IsConnected on the base
        var prop = typeof(WebSocketClientBase).GetProperty("IsConnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var isConnected = (bool)prop!.GetValue(client)!;
        Assert.False(isConnected);
        client.Dispose();
    }

    [Fact]
    public void IsConnected_FalseAfterDispose()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.Dispose();
        var prop = typeof(WebSocketClientBase).GetProperty("IsConnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var isConnected = (bool)prop!.GetValue(client)!;
        Assert.False(isConnected);
    }

    [Fact]
    public async Task ConnectAsync_RaisesStatusChangedConnecting()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        // ConnectAsync should always emit Connecting.
        // Depending on timing/shutdown races, it may then emit Error or be canceled.
        await client.ConnectAsync();

        Assert.Contains(ConnectionStatus.Connecting, statuses);
        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectionFails_StartsReconnectLoop()
    {
        var client = new TestWebSocketClient("ws://127.0.0.1:1", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        await client.ConnectAsync();
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.True(statuses.Count(s => s == ConnectionStatus.Connecting) >= 2);
        Assert.Contains(_logger.Logs, line => line.Contains("reconnecting in 1", StringComparison.OrdinalIgnoreCase) && line.Contains("ms (attempt 1)", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WhenUpgradeResponseStalls_TimesOutAndRetries()
    {
        using var server = new ControlledUpgradeWebSocketServer(int.MaxValue);
        using var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromMilliseconds(250),
        };
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, status) => statuses.Enqueue(status);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await server.FirstStalledClientClosed.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(
            () => server.RequestCount >= 2,
            TimeSpan.FromSeconds(3));

        Assert.Equal(0, client.OnConnectedCallCount);
        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.True(statuses.Count(status => status == ConnectionStatus.Connecting) >= 2);
        Assert.Contains(
            _logger.Logs,
            line => line.Contains(
                "test connect timed out after",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectAsync_DisposeDuringStalledUpgrade_IsShutdownNotTimeout()
    {
        using var server = new ControlledUpgradeWebSocketServer(int.MaxValue);
        var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromSeconds(10),
        };
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, status) => statuses.Enqueue(status);

        var connect = client.ConnectAsync();
        await server.FirstRequestReceived.WaitAsync(TimeSpan.FromSeconds(2));

        client.Dispose();
        await connect.WaitAsync(TimeSpan.FromSeconds(2));
        await server.FirstStalledClientClosed.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        Assert.DoesNotContain(ConnectionStatus.Error, statuses);
        Assert.Equal(1, server.RequestCount);
        Assert.DoesNotContain(
            _logger.Logs,
            line => line.Contains("connect timed out", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            _logger.Logs,
            line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectAsync_TimedOutRetry_DoesNotDisposeNewerConnectionDuringBackoff()
    {
        using var server = new ControlledUpgradeWebSocketServer(stalledUpgradeCount: 1);
        using var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromMilliseconds(250),
        };
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, status) => statuses.Enqueue(status);

        var timedOutConnect = client.ConnectAsync();
        await server.FirstRequestReceived.WaitAsync(TimeSpan.FromSeconds(2));
        await timedOutConnect.WaitAsync(TimeSpan.FromSeconds(2));
        var errorStatusesAfterTimeout =
            statuses.Count(status => status == ConnectionStatus.Error);
        Assert.True(errorStatusesAfterTimeout >= 1);

        var currentConnect = client.ConnectAsync();
        await server.UpgradeCompleted.WaitAsync(TimeSpan.FromSeconds(2));
        await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.True(client.TestIsConnected);
        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(
            errorStatusesAfterTimeout,
            statuses.Count(status => status == ConnectionStatus.Error));

        await server.SendTextAsync("current-socket-message");
        await WaitForConditionAsync(
            () => client.ProcessedMessages.Contains("current-socket-message"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_TimedOutRetry_DoesNotReplaceNewerConnectionAfterAuthorization()
    {
        using var server = new ControlledUpgradeWebSocketServer(stalledUpgradeCount: 1);
        using var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromMilliseconds(250),
        };
        var authorizationEntered =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReconnectAuthorizationAsync = async cancellationToken =>
        {
            authorizationEntered.TrySetResult();
            await releaseAuthorization.Task.WaitAsync(cancellationToken);
            return ReconnectAuthorizationResult.AllowedResult;
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await authorizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var currentConnect = client.ConnectAsync();
        await server.UpgradeCompleted.WaitAsync(TimeSpan.FromSeconds(2));
        await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));

        releaseAuthorization.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.True(client.TestIsConnected);
        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.Equal(2, server.RequestCount);

        await server.SendTextAsync("authorized-current-socket-message");
        await WaitForConditionAsync(
            () => client.ProcessedMessages.Contains("authorized-current-socket-message"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_StaleAuthorizationDenial_DoesNotFailNewerConnection()
    {
        using var server = new ControlledUpgradeWebSocketServer(stalledUpgradeCount: 1);
        using var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromMilliseconds(250),
        };
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, status) => statuses.Enqueue(status);
        var authorizationEntered =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizationResult =
            new TaskCompletionSource<ReconnectAuthorizationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReconnectAuthorizationAsync = async cancellationToken =>
        {
            authorizationEntered.TrySetResult();
            return await authorizationResult.Task.WaitAsync(cancellationToken);
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await authorizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var errorsAfterTimeout =
            statuses.Count(status => status == ConnectionStatus.Error);

        var currentConnect = client.ConnectAsync();
        await server.UpgradeCompleted.WaitAsync(TimeSpan.FromSeconds(2));
        await currentConnect.WaitAsync(TimeSpan.FromSeconds(2));

        authorizationResult.TrySetResult(new ReconnectAuthorizationResult(
            false,
            GatewayErrorKind.LocalPortConflict,
            "stale denial"));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.True(client.TestIsConnected);
        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(
            errorsAfterTimeout,
            statuses.Count(status => status == ConnectionStatus.Error));
        Assert.DoesNotContain(
            _logger.Logs,
            line => line.Contains(
                "reconnect blocked by endpoint authorization policy",
                StringComparison.OrdinalIgnoreCase));

        await server.SendTextAsync("denied-current-socket-message");
        await WaitForConditionAsync(
            () => client.ProcessedMessages.Contains("denied-current-socket-message"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_NewerFailedAttempt_DoesNotAbandonActiveReconnectLoop()
    {
        using var server = new ControlledUpgradeWebSocketServer(
            ControlledUpgradeBehavior.Stall,
            ControlledUpgradeBehavior.Stall,
            ControlledUpgradeBehavior.Close,
            ControlledUpgradeBehavior.Upgrade);
        using var client = new TestWebSocketClient(server.WebSocketUrl, "token", _logger)
        {
            TestConnectAttemptTimeout = TimeSpan.FromMilliseconds(250),
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(
            () => server.RequestCount >= 2,
            TimeSpan.FromSeconds(3));

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await server.UpgradeCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => client.TestIsConnected,
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.Equal(4, server.RequestCount);

        await server.SendTextAsync("recovered-after-newer-failure");
        await WaitForConditionAsync(
            () => client.ProcessedMessages.Contains("recovered-after-newer-failure"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_WhenAutoReconnectDisabled_DoesNotStartReconnectLoop()
    {
        var client = new TestWebSocketClient("ws://127.0.0.1:1", "token", _logger)
        {
            AutoReconnectEnabled = false
        };
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        await client.ConnectAsync();
        // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
        await Task.Delay(250);

        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.Single(statuses, s => s == ConnectionStatus.Connecting);
        Assert.DoesNotContain(_logger.Logs, line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_StaleConnectionDoesNotStartListenerOnNewerSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new BlockingFirstConnectClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        var unexpectedErrorStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, s) => statuses.Enqueue(s);
        client.StatusChanged += (_, s) =>
        {
            if (s == ConnectionStatus.Error)
                unexpectedErrorStatus.TrySetResult();
        };

        var firstConnect = client.ConnectAsync();
        await client.FirstConnectEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var secondConnect = client.ConnectAsync();
        await client.SecondConnectReturned.WaitAsync(TimeSpan.FromSeconds(2));

        client.ReleaseFirstConnect();
        await Task.WhenAll(firstConnect, secondConnect).WaitAsync(TimeSpan.FromSeconds(2));

        // If the stale first ConnectAsync starts a listener after the second
        // connection is current, two listeners race on the same ClientWebSocket
        // and one reports a listen error.
        var unexpected = await Task.WhenAny(
            unexpectedErrorStatus.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(2, client.OnConnectedCallCount);
        Assert.NotSame(unexpectedErrorStatus.Task, unexpected);
        Assert.Equal(0, client.OnErrorCallCount);
        Assert.DoesNotContain(ConnectionStatus.Error, statuses);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_DoesNotDisposeNewerConnection_WhenSupersededDuringDelay()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        await client.ConnectAsync();
        await client.SecondConnected.WaitAsync(TimeSpan.FromSeconds(2));

        var staleReconnectWon = await Task.WhenAny(
            client.ThirdConnected,
            Task.Delay(TimeSpan.FromMilliseconds(1800)));

        Assert.NotSame(client.ThirdConnected, staleReconnectWon);
        Assert.Equal(2, client.OnConnectedCallCount);
        Assert.Equal(2, server.AcceptedCount);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_ContinuesAfterFailedRetry_WhenNoNewerConnectionOwnsSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        server.StopAccepting();

        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 4,
            TimeSpan.FromSeconds(4));

        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.True(_logger.Logs.Count(
            line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase)) >= 2);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_ReconnectsCurrentClosingSocket_WhenSupersededLoopIsActive()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        await client.ConnectAsync();
        await client.SecondConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 2, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(1);

        var reconnect = await Task.WhenAny(
            client.ThirdConnected,
            Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(client.ThirdConnected, reconnect);
        await WaitForConditionAsync(
            () => server.AcceptedCount >= 3,
            TimeSpan.FromSeconds(2));

        client.Dispose();
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not met before the timeout.");

            // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
            await Task.Delay(25);
        }
    }
}

[CollectionDefinition("WebSocketClientBase", DisableParallelization = true)]
public sealed class WebSocketClientBaseTestCollection
{
}

internal sealed class BlockingFirstConnectClient : WebSocketClientBase
{
    private readonly TaskCompletionSource _firstConnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnectReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connectCallbacks;

    public int OnConnectedCallCount => Volatile.Read(ref _connectCallbacks);
    public int OnErrorCallCount { get; private set; }
    public Task FirstConnectEntered => _firstConnectEntered.Task;
    public Task SecondConnectReturned => _secondConnectReturned.Task;

    public BlockingFirstConnectClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
    }

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "race-test";
    protected override bool ShouldAutoReconnect() => false;

    protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;

    protected override async Task OnConnectedAsync()
    {
        var count = Interlocked.Increment(ref _connectCallbacks);
        if (count == 1)
        {
            _firstConnectEntered.TrySetResult();
            await _releaseFirstConnect.Task;
            return;
        }

        _secondConnectReturned.TrySetResult();
    }

    protected override void OnError(Exception ex) => OnErrorCallCount++;

    public void ReleaseFirstConnect() => _releaseFirstConnect.TrySetResult();
}

internal sealed class ReconnectBackoffRaceClient : WebSocketClientBase
{
    private readonly TaskCompletionSource _firstConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _thirdConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connectCallbacks;

    public int OnConnectedCallCount => Volatile.Read(ref _connectCallbacks);
    public Task FirstConnected => _firstConnected.Task;
    public Task SecondConnected => _secondConnected.Task;
    public Task ThirdConnected => _thirdConnected.Task;

    public ReconnectBackoffRaceClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
    }

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "reconnect-race-test";
    protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;

    protected override Task OnConnectedAsync()
    {
        var count = Interlocked.Increment(ref _connectCallbacks);
        switch (count)
        {
            case 1:
                _firstConnected.TrySetResult();
                break;
            case 2:
                _secondConnected.TrySetResult();
                break;
            case 3:
                _thirdConnected.TrySetResult();
                break;
        }

        return Task.CompletedTask;
    }
}

internal sealed class LoopbackWebSocketServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TcpListener? _managedListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _acceptedSockets = new();
    private readonly List<TcpClient> _managedClients = new();
    private readonly TaskCompletionSource<WebSocket> _firstAcceptedSocket =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptLoop;

    public string WebSocketUrl { get; }
    public int AcceptedCount
    {
        get
        {
            lock (_acceptedSockets)
            {
                return _acceptedSockets.Count;
            }
        }
    }

    public async Task WaitForAcceptedCountAsync(int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AcceptedCount < expectedCount)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Loopback server accepted {AcceptedCount} connection(s); expected {expectedCount}.");
            }

            // slopwatch-ignore: SW004 This bounded poll synchronizes the test with the async server accept loop.
            await Task.Delay(10);
        }
    }

    public LoopbackWebSocketServer(bool useManagedWebSocket = false)
    {
        var port = GetFreeTcpPort();
        if (useManagedWebSocket)
        {
            _managedListener = new TcpListener(IPAddress.Loopback, port);
        }
        else
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        WebSocketUrl = $"ws://127.0.0.1:{port}/";
    }

    public Task StartAsync()
    {
        if (_managedListener is not null)
        {
            _managedListener.Start();
            _acceptLoop = Task.Run(AcceptManagedLoopAsync);
        }
        else
        {
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            lock (_acceptedSockets)
            {
                _acceptedSockets.Add(wsContext.WebSocket);
            }
            _firstAcceptedSocket.TrySetResult(wsContext.WebSocket);
        }
    }

    private async Task AcceptManagedLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _managedListener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_cts.Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var stream = client.GetStream();
                var request = await ReadHttpHeadersAsync(stream, _cts.Token);
                var key = request
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .First(line => line.StartsWith(
                        "Sec-WebSocket-Key:",
                        StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1]
                    .Trim();
                var accept = Convert.ToBase64String(
                    SHA1.HashData(
                        Encoding.ASCII.GetBytes(
                            $"{key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                await stream.WriteAsync(response.AsMemory(), _cts.Token);

                var socket = WebSocket.CreateFromStream(
                    stream,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));
                lock (_acceptedSockets)
                {
                    _managedClients.Add(client);
                    _acceptedSockets.Add(socket);
                }

                _firstAcceptedSocket.TrySetResult(socket);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        const int maxHeaderBytes = 16 * 1024;
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(next.AsMemory(), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("WebSocket handshake ended before the HTTP headers.");

            bytes.Add(next[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == '\r' &&
                bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' &&
                bytes[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }

        throw new InvalidOperationException("WebSocket handshake headers exceeded the test limit.");
    }

    public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken = default)
    {
        var socket = await _firstAcceptedSocket.Task.WaitAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
            throw new InvalidOperationException("Expected one complete WebSocket text message.");

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
        var socket = await _firstAcceptedSocket.Task.WaitAsync(cancellationToken);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    public Task CloseSocketAsync(int index) =>
        CloseSocketAsync(
            index,
            WebSocketCloseStatus.NormalClosure,
            "test close");

    public async Task CloseSocketAsync(
        int index,
        WebSocketCloseStatus closeStatus,
        string closeStatusDescription)
    {
        WebSocket socket;
        lock (_acceptedSockets)
        {
            socket = _acceptedSockets[index];
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                closeStatus,
                closeStatusDescription,
                CancellationToken.None);
        }
    }

    public void StopAccepting()
    {
        _cts.Cancel();
        try { _managedListener?.Stop(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _managedListener?.Stop(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        lock (_acceptedSockets)
        {
            foreach (var socket in _acceptedSockets)
            {
                try { socket.Dispose(); } catch { }
            }

            foreach (var client in _managedClients)
            {
                try { client.Dispose(); } catch { }
            }
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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
}

internal enum ControlledUpgradeBehavior
{
    Stall,
    Close,
    Upgrade,
}

internal sealed class ControlledUpgradeWebSocketServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<int, ControlledUpgradeBehavior> _behaviorForRequest;
    private readonly object _connectionsLock = new();
    private readonly List<TcpClient> _clients = new();
    private readonly List<WebSocket> _webSockets = new();
    private readonly TaskCompletionSource _firstRequestReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstStalledClientClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebSocket> _upgradeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private int _requestCount;

    public string WebSocketUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    public Task FirstRequestReceived => _firstRequestReceived.Task;
    public Task FirstStalledClientClosed => _firstStalledClientClosed.Task;
    public Task UpgradeCompleted => _upgradeCompleted.Task;

    public ControlledUpgradeWebSocketServer(int stalledUpgradeCount)
        : this(requestNumber =>
            requestNumber <= stalledUpgradeCount
                ? ControlledUpgradeBehavior.Stall
                : ControlledUpgradeBehavior.Upgrade)
    {
    }

    public ControlledUpgradeWebSocketServer(
        params ControlledUpgradeBehavior[] behaviors)
        : this(CreateBehaviorSelector(behaviors))
    {
    }

    private static Func<int, ControlledUpgradeBehavior> CreateBehaviorSelector(
        ControlledUpgradeBehavior[] behaviors)
    {
        if (behaviors.Length == 0)
            throw new ArgumentException("At least one upgrade behavior is required.", nameof(behaviors));

        return requestNumber =>
            requestNumber <= behaviors.Length
                ? behaviors[requestNumber - 1]
                : behaviors[^1];
    }

    private ControlledUpgradeWebSocketServer(
        Func<int, ControlledUpgradeBehavior> behaviorForRequest)
    {
        _behaviorForRequest = behaviorForRequest;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        WebSocketUrl = $"ws://127.0.0.1:{port}/";
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_cts.Token.IsCancellationRequested)
            {
                return;
            }

            lock (_connectionsLock)
            {
                _clients.Add(client);
            }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        WebSocket? webSocket = null;
        var requestNumber = 0;
        var stalled = false;
        try
        {
            var stream = client.GetStream();
            var request = await ReadHttpHeadersAsync(stream, _cts.Token);
            requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                _firstRequestReceived.TrySetResult();
            }

            var behavior = _behaviorForRequest(requestNumber);
            if (behavior == ControlledUpgradeBehavior.Close)
            {
                return;
            }

            if (behavior == ControlledUpgradeBehavior.Stall)
            {
                stalled = true;
                var buffer = new byte[1];
                while (await stream.ReadAsync(buffer, _cts.Token) > 0)
                {
                }
                return;
            }

            var key = request
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .First(line => line.StartsWith(
                    "Sec-WebSocket-Key:",
                    StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1]
                .Trim();
            var accept = Convert.ToBase64String(
                SHA1.HashData(
                    Encoding.ASCII.GetBytes(
                        $"{key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response, _cts.Token);

            webSocket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(30));
            lock (_connectionsLock)
            {
                _webSockets.Add(webSocket);
            }
            _upgradeCompleted.TrySetResult(webSocket);
            await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (requestNumber == 1 && stalled)
            {
                _firstStalledClientClosed.TrySetResult();
            }

            try { webSocket?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        const int maxHeaderBytes = 16 * 1024;
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(next, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("WebSocket handshake ended before the HTTP headers.");

            bytes.Add(next[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == '\r' &&
                bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' &&
                bytes[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }

        throw new InvalidOperationException("WebSocket handshake headers exceeded the test limit.");
    }

    public async Task SendTextAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var socket = await _upgradeCompleted.Task.WaitAsync(cancellationToken);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_connectionsLock)
        {
            foreach (var socket in _webSockets)
            {
                try { socket.Dispose(); } catch { }
            }
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
        }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }
}

public class TestLogger : IOpenClawLogger
{
    public List<string> Logs { get; } = new();
    public void Info(string message) => Logs.Add($"INFO: {message}");
    public void Debug(string message) => Logs.Add($"DEBUG: {message}");
    public void Warn(string message) => Logs.Add($"WARN: {message}");
    public void Error(string message, Exception? ex = null) => Logs.Add($"ERROR: {message}");
}

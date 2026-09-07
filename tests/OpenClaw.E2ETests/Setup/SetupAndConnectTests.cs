using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.E2ETests;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// Defines the xUnit test collection that shares the E2ESetupFixture.
/// All tests in this collection run against a single setup pipeline execution.
/// </summary>
[CollectionDefinition("E2E Setup")]
public class E2ESetupCollection : ICollectionFixture<E2ESetupFixture> { }

/// <summary>
/// Validates that a headless first-time setup produces a working tray
/// with connected operator and node, verified via MCP tool calls.
/// </summary>
[Collection("E2E Setup")]
public class SetupAndConnectTests
{
    private readonly E2ESetupFixture _fixture;

    public SetupAndConnectTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;

        // Fail fast if the fixture didn't initialize cleanly
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public async Task FullSetup_TrayConnects_OperatorAndNode()
    {
        // Call app.status and verify the tray is fully connected
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        var root = doc.RootElement;

        // Log full response for debugging
        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.status response: {rawJson}");

        AssertReadyStatus(root);
        AssertOperatorCanApproveNodeTrust(root);
    }

    [E2EFact]
    public async Task FullSetup_NodeCapabilities_Propagated()
    {
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.nodes");
        var root = doc.RootElement;

        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.nodes response: {rawJson}");

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 1,
            $"Expected at least 1 node, got {root.GetArrayLength()}; response: {rawJson}");

        var windowsNode = FindWindowsNode(root);
        var expectedCapabilities = new CapabilitiesConfig()
            .GetEnabledCapabilities()
            .Select(c => c.Category)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedCommands = new CapabilitiesConfig().GetEnabledCommandIds().ToArray();

        var actualCapabilities = ReadStringArray(windowsNode.GetProperty("Capabilities"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actualCommands = ReadStringArray(windowsNode.GetProperty("Commands"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedCapabilities, actualCapabilities);
        Assert.Equal(expectedCommands, actualCommands);

        var capCount = windowsNode.GetProperty("CapabilityCount").GetInt32();
        Assert.Equal(expectedCapabilities.Length, capCount);
        var commandCount = windowsNode.GetProperty("CommandCount").GetInt32();
        Assert.Equal(expectedCommands.Length, commandCount);

        var isOnline = windowsNode.GetProperty("IsOnline").GetBoolean();
        Assert.True(isOnline,
            $"Expected node IsOnline=true; node: {windowsNode.GetRawText()}");
    }

    [E2EFact]
    public async Task FullSetup_WslAndGatewayConfiguration_FilesValidated()
    {
        var wslConf = await _fixture.RunInWslAsync("cat /etc/wsl.conf", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(wslConf, "read /etc/wsl.conf");
        Console.WriteLine($"[E2E] /etc/wsl.conf:\n{wslConf.Stdout}");
        Assert.Contains("systemd=true", wslConf.Stdout);
        Assert.Contains("enabled=false", wslConf.Stdout);
        Assert.Contains("appendWindowsPath=false", wslConf.Stdout);
        Assert.Contains("default=openclaw", wslConf.Stdout);
        Assert.Contains("useWindowsTimezone=true", wslConf.Stdout);

        var agentsContext = await _fixture.RunInWslAsync(
            "cat /home/openclaw/.openclaw/workspace/AGENTS.md",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(agentsContext, "read managed Windows node context");
        Assert.Contains("<!-- BEGIN OPENCLAW WINDOWS NODE CONTEXT: managed by OpenClaw Windows setup -->", agentsContext.Stdout);
        Assert.Contains("exec host=node", agentsContext.Stdout);
        Assert.Contains("<!-- END OPENCLAW WINDOWS NODE CONTEXT -->", agentsContext.Stdout);
        Assert.DoesNotContain("tools.exec.security full", agentsContext.Stdout);

        var openClawJsonProbe = await _fixture.RunInWslAsync(
            "paths=$(find /home/openclaw/.openclaw /opt/openclaw /etc/openclaw -type f -name openclaw.json 2>/dev/null | sort); if [ -z \"$paths\" ]; then echo 'OPENCLAW_JSON_PATH:<not-found>'; else for path in $paths; do echo OPENCLAW_JSON_PATH:$path; cat \"$path\"; done; fi",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(openClawJsonProbe, "probe WSL openclaw.json");
        Console.WriteLine($"[E2E] WSL openclaw.json probe:\n{openClawJsonProbe.Stdout}");

        if (openClawJsonProbe.Stdout.Contains('{', StringComparison.Ordinal))
        {
            var jsonStart = openClawJsonProbe.Stdout.IndexOf('{');
            using var configDoc = JsonDocument.Parse(openClawJsonProbe.Stdout[jsonStart..]);
            var root = configDoc.RootElement;
            AssertJsonPath(root, ["gateway", "port"], _fixture.GatewayPort.ToString());
            AssertJsonPath(root, ["gateway", "bind"], "loopback");
            AssertJsonPath(root, ["gateway", "auth", "mode"], "token");

            var allowCommandsKey = ResolveNodeCommandsAllowKey();
            var allowCommands = ReadStringArray(GetJsonPath(root, allowCommandsKey.Split('.')));
            Assert.Equal(new CapabilitiesConfig().GetEnabledCommandIds().ToArray(), allowCommands.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        var gatewayPort = await _fixture.RunInWslAsync("openclaw config get gateway.port", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayPort, "read gateway.port");
        Assert.Contains(_fixture.GatewayPort.ToString(), gatewayPort.Stdout);

        var gatewayBind = await _fixture.RunInWslAsync("openclaw config get gateway.bind", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayBind, "read gateway.bind");
        Assert.Contains("loopback", gatewayBind.Stdout);

        var gatewayAuthMode = await _fixture.RunInWslAsync("openclaw config get gateway.auth.mode", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayAuthMode, "read gateway.auth.mode");
        Assert.Contains("token", gatewayAuthMode.Stdout);

        var nodeCommandsAllowKey = ResolveNodeCommandsAllowKey();
        var cliAllowCommands = await _fixture.RunInWslAsync(
            $"openclaw config get {nodeCommandsAllowKey}",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(cliAllowCommands, $"read {nodeCommandsAllowKey}");
        Console.WriteLine($"[E2E] {nodeCommandsAllowKey}: {cliAllowCommands.Stdout}");
        var expectedCommands = new CapabilitiesConfig().GetEnabledCommandIds().ToArray();
        var effectiveCommands = ParseJsonArrayFromOutput(cliAllowCommands.Stdout);
        Assert.Equal(expectedCommands, effectiveCommands.Order(StringComparer.OrdinalIgnoreCase).ToArray());

        var gateway = _fixture.ReadActiveGatewayRecord();
        Assert.Equal($"ws://127.0.0.1:{_fixture.GatewayPort}", gateway.GatewayUrl);

        var settingsPath = Path.Combine(_fixture.DataDir, "settings.json");
        var gatewaysPath = Path.Combine(_fixture.DataDir, "gateways.json");
        Console.WriteLine($"[E2E] settings.json path: {settingsPath}");
        Console.WriteLine($"[E2E] gateways.json path: {gatewaysPath}; activeId={gateway.ActiveId}; sharedTokenLength={gateway.SharedGatewayToken?.Length ?? 0}");
        Assert.True(File.Exists(settingsPath));
        Assert.True(File.Exists(gatewaysPath));

        var identityDir = Path.Combine(_fixture.DataDir, "gateways", gateway.ActiveId);
        Assert.True(Directory.Exists(identityDir), $"Expected identity directory: {identityDir}");
        Assert.Contains(Directory.EnumerateFiles(identityDir), path => Path.GetFileName(path).Contains("device-key", StringComparison.OrdinalIgnoreCase));
    }

    [E2EFact]
    public async Task FullSetup_AlreadyRunningGateway_IsRecognizedAsServiceOwned()
    {
        var listeners = await _fixture.RunInWslAsync(
            $"ss -H -ltnp 'sport = :{_fixture.GatewayPort}'",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(listeners, "inspect running gateway listeners");

        var mainPid = await _fixture.RunInWslAsync(
            "systemctl --user show openclaw-gateway.service -p MainPID --value",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(mainPid, "read gateway service MainPID");

        var parsedMainPid = mainPid.Stdout.Trim();
        Assert.True(int.TryParse(parsedMainPid, out var pid) && pid > 0, $"Invalid gateway MainPID: {parsedMainPid}");
        var listenerPids = Regex.Matches(listeners.Stdout, @"pid=(\d+),")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();
        Assert.NotEmpty(listenerPids);
        Assert.All(listenerPids, listenerPid => Assert.Equal(pid, listenerPid));

        var proofLogPath = Path.Combine(_fixture.ArtifactDir, "service-owned-gateway-start.jsonl");
        var config = SetupConfig.LoadFromFile(_fixture.ConfigPath);
        using var logger = new SetupLogger(proofLogPath, LogLevel.Trace);
        using var journal = new TransactionJournal(filePath: null, logger);
        var context = new SetupContext(
            config,
            logger,
            journal,
            new CommandRunner(logger),
            CancellationToken.None,
            _fixture.DataDir,
            _fixture.LocalAppDataRoot)
        {
            DistroName = _fixture.DistroName,
        };

        var result = await new StartGatewayStep().ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var proofLog = await File.ReadAllTextAsync(proofLogPath);
        Assert.Contains(
            $"Port {_fixture.GatewayPort} is owned by openclaw-gateway.service (PID {pid}).",
            proofLog,
            StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task FullSetup_ForeignWslListener_IsRejectedBeforeGatewayStart()
    {
        var config = SetupConfig.LoadFromFile(_fixture.ConfigPath);
        var port = GetFreeTcpPort();
        var nodePath = $"/home/{config.Wsl.User}/.openclaw/tools/node/bin/node";
        var start = await _fixture.RunInWslAsync(
            $"""
            nohup '{nodePath}' -e 'require("net").createServer().listen({port}, "127.0.0.1")' \
              >/tmp/openclaw-foreign-listener-{port}.log 2>&1 </dev/null &
            echo $!
            """,
            TimeSpan.FromSeconds(15),
            inputViaStdin: true);
        AssertCommandSucceeded(start, "start foreign WSL listener");
        Assert.True(int.TryParse(start.Stdout.Trim(), out var foreignPid) && foreignPid > 0);

        try
        {
            OpenClaw.SetupEngine.CommandResult? listeners = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                listeners = await _fixture.RunInWslAsync(
                    $"ss -H -ltnp 'sport = :{port}'",
                    TimeSpan.FromSeconds(15));
                if (listeners.ExitCode == 0 &&
                    listeners.Stdout.Contains($"pid={foreignPid},", StringComparison.Ordinal))
                {
                    break;
                }
                await Task.Delay(100);
            }
            var verifiedListeners = Assert.IsType<OpenClaw.SetupEngine.CommandResult>(listeners);
            AssertCommandSucceeded(verifiedListeners, "inspect foreign WSL listener");
            Assert.Contains($"pid={foreignPid},", verifiedListeners.Stdout, StringComparison.Ordinal);

            config.GatewayPort = port;
            var proofLogPath = Path.Combine(_fixture.ArtifactDir, "foreign-gateway-listener.jsonl");
            using var logger = new SetupLogger(proofLogPath, LogLevel.Trace);
            using var journal = new TransactionJournal(filePath: null, logger);
            var context = new SetupContext(
                config,
                logger,
                journal,
                new CommandRunner(logger),
                CancellationToken.None,
                _fixture.DataDir,
                _fixture.LocalAppDataRoot)
            {
                DistroName = _fixture.DistroName,
            };

            var result = await new StartGatewayStep().ExecuteAsync(context, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains($"Port {port} is already in use by another process.", result.Message);
            var proofLog = await File.ReadAllTextAsync(proofLogPath);
            Assert.DoesNotContain("openclaw gateway start", proofLog, StringComparison.Ordinal);
        }
        finally
        {
            _ = await _fixture.RunInWslAsync(
                $"kill {foreignPid} 2>/dev/null || true",
                TimeSpan.FromSeconds(15));
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ResolveNodeCommandsAllowKey()
    {
        var gatewayVersion =
            Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION") ??
            GatewayReleasePolicy.RecommendedVersion;
        return ConfigureGatewayStep.ResolveNodeCommandsAllowKey(gatewayVersion);
    }

    [E2EFact]
    public async Task FullSetup_WindowsNodeContext_RemainsIdempotentAfterCrlfEdit()
    {
        const string agents = "/home/openclaw/.openclaw/workspace/AGENTS.md";
        const string beginMarker = "<!-- BEGIN OPENCLAW WINDOWS NODE CONTEXT: managed by OpenClaw Windows setup -->";
        const string endMarker = "<!-- END OPENCLAW WINDOWS NODE CONTEXT -->";
        var convert = await _fixture.RunInWslAsync(
            $"tmp=$(mktemp); {{ printf 'CRLF_SENTINEL\\r\\n'; awk '{{ printf \"%s\\r\\n\", $0 }}' {agents}; }} > \"$tmp\"; mv \"$tmp\" {agents}",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true);
        AssertCommandSucceeded(convert, "convert AGENTS.md to CRLF");

        var config = SetupConfig.LoadFromFile(_fixture.ConfigPath);
        using var logger = new SetupLogger(filePath: null);
        using var journal = new TransactionJournal(filePath: null, logger);
        var context = new SetupContext(
            config,
            logger,
            journal,
            new CommandRunner(logger),
            CancellationToken.None,
            _fixture.DataDir,
            _fixture.LocalAppDataRoot);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(context, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Message);

        var probe = await _fixture.RunInWslAsync(
            $"awk 'BEGIN {{ b=0; e=0 }} {{ line=$0; sub(/\\r$/, \"\", line); if (line == \"{beginMarker}\") b++; if (line == \"{endMarker}\") e++ }} END {{ print b, e }}' {agents}; grep -q $'CRLF_SENTINEL\\r$' {agents}",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true);
        AssertCommandSucceeded(probe, "verify CRLF context replacement");
        Assert.Contains("1 1", probe.Stdout);
    }

    [E2EFact]
    public async Task FullSetup_TrayEnsuresWslKeepAlive()
    {
        var logLine = await _fixture.WaitForTrayKeepAliveReadyAsync();
        Assert.Contains(_fixture.DistroName, logLine);

        var keepAlive = await _fixture.RunInWslAsync(
            "ps -ef | grep '[s]leep infinity'",
            TimeSpan.FromSeconds(15));

        AssertCommandSucceeded(keepAlive, "verify WSL keepalive process");
        Console.WriteLine($"[E2E] WSL keepalive process:\n{keepAlive.Stdout}");
        Assert.Contains("sleep infinity", keepAlive.Stdout);
    }

    [E2EFact]
    public async Task FullSetup_DashboardLink_UsesSharedGatewayTokenFragmentAfterPairing()
    {
        using var dashboardDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.dashboard.url");
        var dashboard = dashboardDoc.RootElement;
        var dashboardUrl = dashboard.GetProperty("url").GetString();
        var credentialSource = dashboard.GetProperty("credentialSource").GetString();
        var usesSharedGatewayToken = dashboard.GetProperty("usesSharedGatewayToken").GetBoolean();
        var hasTokenQuery = dashboard.GetProperty("hasTokenQuery").GetBoolean();

        Assert.Equal("record.SharedGatewayToken", credentialSource);
        Assert.True(usesSharedGatewayToken, $"Expected tray dashboard link to use the shared HTTP gateway token; source={credentialSource}");
        Assert.False(hasTokenQuery, $"Expected tray dashboard link to avoid token query strings; source={credentialSource}");
        Assert.NotNull(dashboardUrl);
        Assert.Contains($":{_fixture.GatewayPort}", dashboardUrl);
        Assert.True(
            dashboardUrl!.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
            dashboardUrl.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase),
            $"Expected dashboard URL to use a loopback host, got {dashboardUrl}");
        Assert.Contains("#token=", dashboardUrl);
        Console.WriteLine($"[E2E] tray dashboard URL source={credentialSource}; tokenQuery={hasTokenQuery}; length={dashboardUrl.Length}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync(dashboardUrl);
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[E2E] tray-generated dashboard URL HTTP status: {(int)response.StatusCode}; body length={body.Length}");
        Assert.True(response.IsSuccessStatusCode, $"Expected dashboard/shared-token request to succeed, got HTTP {(int)response.StatusCode}");
        Assert.DoesNotContain("incorrect token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unauthorized", body, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task FullSetup_GatewayCliShowsPairedDeviceAndNode()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var devices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(devices, "list gateway devices");
        Console.WriteLine($"[E2E] openclaw devices list --json:\n{devices.Stdout}");
        AssertNoPendingRequests(devices.Stdout);

        var nodes = await _fixture.RunInWslAsync("openclaw nodes list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(nodes, "list gateway nodes");
        Console.WriteLine($"[E2E] openclaw nodes list --json:\n{nodes.Stdout}");
        AssertNoPendingRequests(nodes.Stdout);
        Assert.Contains("windows", nodes.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task FullSetup_SafeNodeInvocation_RoutesThroughRealGateway()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.which",
            @params = new { bins = new[] { "cmd" } },
            timeoutMs = 30_000,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout 60000",
            TimeSpan.FromSeconds(70),
            env,
            inputViaStdin: true);
        AssertCommandSucceeded(invoke, "invoke Windows node system.which through real gateway");

        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
            Assert.True(ok.GetBoolean(), $"Expected gateway node.invoke ok=true: {invokeDoc.RootElement.GetRawText()}");

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        var bins = payload.GetProperty("bins");
        Assert.True(bins.TryGetProperty("cmd", out var cmdPath), $"system.which did not return cmd: {payload.GetRawText()}");
        Assert.Contains("cmd.exe", cmdPath.GetString(), StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"[E2E] gateway system.which resolved cmd to {cmdPath.GetString()}");
    }

    [OllamaGatewayE2EFact]
    public async Task RealGateway_OllamaPermission_AllowsThenRejectsBeforeHttpDispatch()
    {
        await using var ollama = FakeOllamaServer.Start();
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var allowCommandsKey = ResolveNodeCommandsAllowKey();
        var originalAllowResult = await _fixture.RunInWslAsync(
            $"openclaw config get {allowCommandsKey} --json",
            TimeSpan.FromSeconds(30),
            env);
        AssertCommandSucceeded(originalAllowResult, $"read {allowCommandsKey} before Ollama proof");
        var originalAllowCommands = ParseJsonArrayFromOutput(originalAllowResult.Stdout).ToArray();
        var proofAllowCommands = originalAllowCommands
            .Append(OllamaCapability.ModelsCommand)
            .Append(OllamaCapability.ChatCommand)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allowChanged = false;
        var permissionEnabled = false;

        try
        {
            allowChanged = true;
            await SetGatewayAllowCommandsAsync(allowCommandsKey, proofAllowCommands, env);
            await SetOllamaPermissionAsync(enabled: true);
            permissionEnabled = true;

            await ReconnectNodeForOllamaPermissionAsync();
            await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
            await ApproveNodeCommandUntilEffectiveAsync(
                nodeId,
                OllamaCapability.ChatCommand,
                TimeSpan.FromSeconds(90));

            var enabledInvoke = await InvokeOllamaChatThroughGatewayAsync(
                nodeId,
                FakeOllamaServer.Model,
                env);
            AssertCommandSucceeded(enabledInvoke, "invoke ollama.chat through real gateway");
            using (var invokeDoc = JsonDocument.Parse(ExtractJsonObject(enabledInvoke.Stdout)))
            {
                if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
                    Assert.True(ok.GetBoolean(), "Gateway ollama.chat response was not successful.");

                var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
                Assert.Equal("ollama", payload.GetProperty("provider").GetString());
                Assert.Equal(FakeOllamaServer.Model, payload.GetProperty("model").GetString());
                Assert.Equal(
                    FakeOllamaServer.ExpectedResponse,
                    payload.GetProperty("response").GetString());
                var usage = payload.GetProperty("usage");
                var timings = payload.GetProperty("timings");
                Console.WriteLine(
                    "[E2E] gateway ollama.chat allowed: " +
                    $"ok=true modelMatched=true responseMatched=true " +
                    $"promptTokens={usage.GetProperty("promptTokens").GetInt32()} " +
                    $"completionTokens={usage.GetProperty("completionTokens").GetInt32()} " +
                    $"totalMs={timings.GetProperty("totalMs").GetDouble():F2}");
            }
            Assert.Equal(1, ollama.ChatRequestCount);
            Assert.NotNull(ollama.LastChatBody);

            ollama.PauseNextChatResponse();
            Task<JsonDocument> capturedMcpCall = _fixture.Client!.CallToolExpectSuccessAsync(
                OllamaCapability.ChatCommand,
                new
                {
                    model = FakeOllamaServer.Model,
                    prompt = FakeOllamaServer.CapturedPrompt,
                    maxTokens = 32,
                    temperature = 0,
                    timeoutMs = 120_000,
                });
            await ollama.WaitForPausedChatAsync(TimeSpan.FromSeconds(15));

            await SetOllamaPermissionAsync(enabled: false);
            permissionEnabled = false;
            await ReconnectNodeForOllamaPermissionAsync();
            await WaitForNodeCommandAsync(
                nodeId,
                OllamaCapability.ChatCommand,
                expectedPresent: false,
                TimeSpan.FromSeconds(90));
            InvalidOperationException revoked = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await capturedMcpCall.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Ollama sharing was disabled", revoked.Message, StringComparison.Ordinal);
            ollama.ReleasePausedChat();
            Console.WriteLine(
                "[E2E] local MCP captured ollama.chat revoked: " +
                "admitted=true loopbackReached=true responseDelivered=false");

            int dispatchCountBefore = CountTrayNodeInvocations(OllamaCapability.ChatCommand);
            int httpCountBefore = ollama.RequestCount;
            var deniedInvoke = await InvokeOllamaChatThroughGatewayAsync(
                nodeId,
                FakeOllamaServer.Model,
                env);
            string denial = deniedInvoke.Stdout + "\n" + deniedInvoke.Stderr;
            Assert.True(
                deniedInvoke.ExitCode != 0 ||
                denial.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase),
                "Disabled ollama.chat gateway invocation unexpectedly succeeded.");
            Assert.True(
                denial.Contains("not support", StringComparison.OrdinalIgnoreCase) ||
                denial.Contains("not declared", StringComparison.OrdinalIgnoreCase) ||
                denial.Contains("not allowed", StringComparison.OrdinalIgnoreCase),
                "Disabled ollama.chat gateway invocation did not report a command-policy rejection.");
            await Task.Delay(500);
            int dispatchCountAfter = CountTrayNodeInvocations(OllamaCapability.ChatCommand);
            Assert.Equal(dispatchCountBefore, dispatchCountAfter);
            Assert.Equal(httpCountBefore, ollama.RequestCount);
            Console.WriteLine(
                "[E2E] gateway ollama.chat denied: " +
                "commandAbsent=true gatewayRejected=true " +
                "nodeDispatchUnchanged=true httpRequestCountUnchanged=true");
        }
        finally
        {
            if (permissionEnabled)
                await SetOllamaPermissionAsync(enabled: false);
            if (allowChanged)
                await SetGatewayAllowCommandsAsync(allowCommandsKey, originalAllowCommands, env);
        }
    }

    [E2EFact]
    public async Task FullSetup_GatewayRestart_ReconnectsTrayAndNode()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var restart = await _fixture.RunInWslAsync(
            "openclaw gateway restart || (systemctl --user restart openclaw-gateway.service && echo restarted-via-systemctl)",
            TimeSpan.FromSeconds(60),
            env);
        AssertCommandSucceeded(restart, "restart real WSL gateway");
        Console.WriteLine($"[E2E] gateway restart output:\n{restart.Stdout}");

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(90));

        using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        AssertReadyStatus(statusDoc.RootElement);
    }

    [E2EFact]
    public async Task RealGateway_QrSetupCodeFlow_ReconnectsThroughTrayMcp()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var setupCode = await MintRealGatewaySetupCodeAsync(env, "mint real gateway setup code");

        using var applyDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.applySetupCode",
            new { setupCode });
        var apply = applyDoc.RootElement;
        Console.WriteLine($"[E2E] applySetupCode response: {apply.GetRawText()}");
        Assert.Equal("Success", apply.GetProperty("outcome").GetString());
        var appliedGatewayUrl = apply.GetProperty("gatewayUrl").GetString();
        Assert.NotNull(appliedGatewayUrl);
        Assert.Contains($":{_fixture.GatewayPort}", appliedGatewayUrl, StringComparison.Ordinal);

        var credentials = await _fixture.WaitForDurablePairedCredentialsAsync();
        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();

        Assert.True(credentials.HasOperatorToken, $"Expected operator device token in {credentials.IdentityDir}");
        Assert.True(credentials.HasNodeToken, $"Expected node device token in {credentials.IdentityDir}");
        Assert.False(credentials.HasBootstrapToken, "Bootstrap token should be cleared after both role tokens are durable");
    }

    [E2EFact]
    public async Task RealGateway_ReusedSetupCode_IsSafeAndIdempotentForSameDevice()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var setupCode = await MintRealGatewaySetupCodeAsync(env, "mint real gateway setup code for reuse test");

        using var firstDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.applySetupCode",
            new { setupCode });
        var first = firstDoc.RootElement;
        Console.WriteLine($"[E2E] first applySetupCode response: {first.GetRawText()}");
        Assert.Equal("Success", first.GetProperty("outcome").GetString());
        var firstCredentials = await _fixture.WaitForDurablePairedCredentialsAsync();
        Assert.True(firstCredentials.HasOperatorToken);
        Assert.True(firstCredentials.HasNodeToken);
        Assert.False(firstCredentials.HasBootstrapToken);

        var before = _fixture.ReadActiveGatewayRecord();
        using var secondDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.applySetupCode",
            new { setupCode });
        var second = secondDoc.RootElement;
        Console.WriteLine($"[E2E] second applySetupCode response: {second.GetRawText()}");

        Assert.Equal("Success", second.GetProperty("outcome").GetString());

        var after = _fixture.ReadActiveGatewayRecord();
        Assert.Equal(before.ActiveId, after.ActiveId);
        Assert.Equal(before.SharedGatewayToken, after.SharedGatewayToken);

        var afterCredentials = await _fixture.WaitForDurablePairedCredentialsAsync();
        Assert.True(afterCredentials.HasOperatorToken, $"Expected operator token to survive in {afterCredentials.IdentityDir}");
        Assert.True(afterCredentials.HasNodeToken, $"Expected node token to survive in {afterCredentials.IdentityDir}");
        Assert.False(afterCredentials.HasBootstrapToken);

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
    }

    [E2EFact]
    public async Task ExternalLike_QrOnlyFreshTray_RequiresExplicitDeviceApproval()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var pendingBefore = await ReadPendingDeviceRequestIdsAsync();
        var pendingNodeBefore = await ReadPendingNodeRequestIdsAsync();
        var handledDeviceRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var handledNodeRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var setupCode = await MintRealGatewaySetupCodeAsync(env, "mint real gateway setup code for external-like tray");
        IsolatedTrayInstance? externalTray = null;
        Exception? testFailure = null;

        try
        {
            externalTray = await IsolatedTrayInstance.StartAsync(_fixture.ArtifactDir, "external-qr-only");
            using var applyDoc = await externalTray.Client.CallToolExpectSuccessAsync(
                "app.connection.applySetupCode",
                new { setupCode });
            var apply = applyDoc.RootElement;
            Console.WriteLine($"[E2E] external-like applySetupCode response: {apply.GetRawText()}");
            Assert.Equal("Success", apply.GetProperty("outcome").GetString());

            var active = externalTray.ReadActiveGatewayRecord();
            Assert.NotNull(active.GatewayUrl);
            Assert.Contains($":{_fixture.GatewayPort}", active.GatewayUrl, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(active.SharedGatewayToken),
                "QR-only external-like onboarding must not invent or persist the shared gateway token.");

            var credentials = externalTray.ReadCredentialState();
            Assert.False(credentials.HasNodeToken, "QR-only external-like onboarding should wait for explicit device approval before persisting a node token.");
            Assert.False(credentials.HasOperatorToken,
                "The validated Gateway recommendation's QR-only external-like onboarding does not provide an admin operator token.");
            Assert.True(credentials.HasBootstrapToken,
                "Bootstrap remains as recovery material while explicit approval is pending.");

            using var statusDoc = await externalTray.Client.CallToolExpectSuccessAsync("app.status");
            var status = statusDoc.RootElement;
            var rawStatus = status.GetRawText();
            Assert.False(status.GetProperty("nodeConnected").GetBoolean(), $"Expected nodeConnected=false before approval; status={rawStatus}");
            Assert.False(status.GetProperty("nodePaired").GetBoolean(), $"Expected nodePaired=false before approval; status={rawStatus}");
            Assert.True(status.TryGetProperty("operatorScopes", out var scopes), $"operatorScopes missing: {rawStatus}");
            Assert.DoesNotContain(ReadStringArray(scopes), scope => string.Equals(scope, "operator.admin", StringComparison.OrdinalIgnoreCase));

            var requestId = await WaitForFirstPendingDeviceRequestIdAsync(pendingBefore);
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            using var dashboardDoc = await externalTray.Client.CallToolExpectSuccessAsync("app.dashboard.url");
            var dashboard = dashboardDoc.RootElement;
            Assert.Equal("record.BootstrapToken", dashboard.GetProperty("credentialSource").GetString());
            Assert.False(dashboard.GetProperty("usesSharedGatewayToken").GetBoolean());
            Assert.False(dashboard.GetProperty("hasTokenQuery").GetBoolean());

            using var rejectDoc = await RejectDevicePairingFromConnectionPageAsync(requestId);
            handledDeviceRequestIds.Add(requestId);
            Console.WriteLine($"[E2E] rejected external-like pending device request via Connection page: {rejectDoc.RootElement.GetRawText()}");
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            if (externalTray is not null)
                await externalTray.DisposeAsync();

            try
            {
                await RejectNewPendingApprovalsUntilQuietAsync(
                    pendingBefore,
                    pendingNodeBefore,
                    handledDeviceRequestIds,
                    handledNodeRequestIds,
                    TimeSpan.FromSeconds(45));
            }
            catch (Exception ex) when (testFailure is not null)
            {
                Console.WriteLine($"[E2E] Cleanup after failed external QR-only tray test also failed: {ex}");
            }
        }
    }

    [E2EFact]
    public async Task RealGateway_SharedTokenFlow_ReconnectsThroughTrayMcp()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var sharedGatewayToken = RequireSharedGatewayToken(gateway.SharedGatewayToken);

        using var connectDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.connectSharedToken",
            new
            {
                gatewayUrl = gateway.GatewayUrl,
                token = sharedGatewayToken
            });
        var connect = connectDoc.RootElement;
        Console.WriteLine($"[E2E] connectSharedToken response: {connect.GetRawText()}");
        Assert.Equal("Success", connect.GetProperty("outcome").GetString());
        Assert.Equal(gateway.GatewayUrl, connect.GetProperty("gatewayUrl").GetString());

        var credentials = await _fixture.WaitForDurablePairedCredentialsAsync();
        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();

        using var dashboardDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.dashboard.url");
        var dashboard = dashboardDoc.RootElement;
        Assert.Equal("record.SharedGatewayToken", dashboard.GetProperty("credentialSource").GetString());
        Assert.True(dashboard.GetProperty("usesSharedGatewayToken").GetBoolean());

        Assert.True(credentials.HasOperatorToken, $"Expected operator device token in {credentials.IdentityDir}");
        Assert.True(credentials.HasNodeToken, $"Expected node device token in {credentials.IdentityDir}");
    }

    [E2EFact]
    public async Task ExternalLike_FreshTray_SharedTokenFlow_PairsOperatorAndNode()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var sharedGatewayToken = RequireSharedGatewayToken(gateway.SharedGatewayToken);
        var pendingBefore = await ReadPendingDeviceRequestIdsAsync();
        var pendingNodeBefore = await ReadPendingNodeRequestIdsAsync();
        var handledDeviceRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var handledNodeRequestIds = new HashSet<string>(StringComparer.Ordinal);
        IsolatedTrayInstance? externalTray = null;
        Exception? testFailure = null;

        try
        {
            externalTray = await IsolatedTrayInstance.StartAsync(_fixture.ArtifactDir, "external-shared-token");
            using var connectDoc = await externalTray.Client.CallToolExpectSuccessAsync(
                "app.connection.connectSharedToken",
                new { gatewayUrl = gateway.GatewayUrl, token = sharedGatewayToken });
            var connect = connectDoc.RootElement;
            Console.WriteLine($"[E2E] external shared-token connect response: {connect.GetRawText()}");
            Assert.Equal("Success", connect.GetProperty("outcome").GetString());

            await ApproveNewPendingDeviceRequestsUntilReadyAsync(pendingBefore, handledDeviceRequestIds, externalTray);
            var nodeRequest = Assert.Single(await ReadNewPendingNodeApprovalsUntilAsync(
                pendingNodeBefore,
                TimeSpan.FromSeconds(30)));
            using (var approve = await ApproveNodePairingFromConnectionPageAsync(nodeRequest.RequestId))
            {
                handledNodeRequestIds.Add(nodeRequest.RequestId);
                Console.WriteLine($"[E2E] explicitly approved external node-trust request via Connection page: {approve.RootElement.GetRawText()}");
            }

            using var reconnectNode = await externalTray.Client.CallToolExpectSuccessAsync("app.connection.reconnectNode");
            Assert.True(reconnectNode.RootElement.GetProperty("reconnected").GetBoolean());
            await externalTray.WaitForConnectionReady(TimeSpan.FromSeconds(120));
            await WaitForNodeEffectiveStateAsync(
                externalTray.Client,
                nodeRequest.NodeId,
                new CapabilitiesConfig { Tts = false },
                TimeSpan.FromSeconds(90));
            AssertExternalTrayDurablePairing(externalTray);
            await AssertGatewayCliStateHealthy();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            if (externalTray is not null)
                await externalTray.DisposeAsync();

            try
            {
                await RejectNewPendingApprovalsUntilQuietAsync(
                    pendingBefore,
                    pendingNodeBefore,
                    handledDeviceRequestIds,
                    handledNodeRequestIds,
                    TimeSpan.FromSeconds(45));
            }
            catch (Exception ex) when (testFailure is not null)
            {
                Console.WriteLine($"[E2E] Cleanup after failed external shared-token tray test also failed: {ex}");
            }
        }
    }

    [E2EFact]
    public async Task RealGateway_BadSharedToken_DoesNotDestroyExistingPairing()
    {
        var before = _fixture.ReadActiveGatewayRecord();
        Assert.False(string.IsNullOrWhiteSpace(before.SharedGatewayToken));
        var beforeCredentials = _fixture.ReadActiveGatewayCredentialState();
        Assert.True(beforeCredentials.HasOperatorToken, $"Expected existing operator token in {beforeCredentials.IdentityDir}");
        Assert.True(beforeCredentials.HasNodeToken, $"Expected existing node token in {beforeCredentials.IdentityDir}");

        using var connectDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.connectSharedToken",
            new
            {
                gatewayUrl = before.GatewayUrl,
                token = "definitely-not-the-real-shared-token"
            });
        var connect = connectDoc.RootElement;
        Console.WriteLine($"[E2E] bad connectSharedToken response: {connect.GetRawText()}");
        Assert.Equal("ConnectionFailed", connect.GetProperty("outcome").GetString());

        var after = _fixture.ReadActiveGatewayRecord();
        Assert.Equal(before.ActiveId, after.ActiveId);
        Assert.Equal(before.GatewayUrl, after.GatewayUrl);
        Assert.Equal(before.SharedGatewayToken, after.SharedGatewayToken);

        var afterCredentials = _fixture.ReadActiveGatewayCredentialState();
        Assert.True(afterCredentials.HasOperatorToken, $"Expected operator token to survive in {afterCredentials.IdentityDir}");
        Assert.True(afterCredentials.HasNodeToken, $"Expected node token to survive in {afterCredentials.IdentityDir}");
        Assert.False(afterCredentials.HasBootstrapToken);

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
    }

    [E2EFact]
    public async Task RealGateway_InvalidSetupCode_DoesNotDestroyExistingPairing()
    {
        var before = _fixture.ReadActiveGatewayRecord();
        var beforeCredentials = _fixture.ReadActiveGatewayCredentialState();
        Assert.True(beforeCredentials.HasOperatorToken, $"Expected existing operator token in {beforeCredentials.IdentityDir}");
        Assert.True(beforeCredentials.HasNodeToken, $"Expected existing node token in {beforeCredentials.IdentityDir}");

        using var applyDoc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.applySetupCode",
            new { setupCode = "this-is-not-a-valid-openclaw-setup-code" });
        var apply = applyDoc.RootElement;
        Console.WriteLine($"[E2E] invalid applySetupCode response: {apply.GetRawText()}");
        Assert.Equal("InvalidCode", apply.GetProperty("outcome").GetString());

        var after = _fixture.ReadActiveGatewayRecord();
        Assert.Equal(before.ActiveId, after.ActiveId);
        Assert.Equal(before.GatewayUrl, after.GatewayUrl);
        Assert.Equal(before.SharedGatewayToken, after.SharedGatewayToken);

        var afterCredentials = _fixture.ReadActiveGatewayCredentialState();
        Assert.True(afterCredentials.HasOperatorToken, $"Expected operator token to survive in {afterCredentials.IdentityDir}");
        Assert.True(afterCredentials.HasNodeToken, $"Expected node token to survive in {afterCredentials.IdentityDir}");
        Assert.False(afterCredentials.HasBootstrapToken);

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
    }

    [E2EFact]
    public async Task FullSetup_OpenClawCommand_IsOnDefaultWslPath()
    {
        var loginShell = await _fixture.RunInWslAsync("bash -lc 'openclaw --version'", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(loginShell, "openclaw --version in login shell");
        Console.WriteLine($"[E2E] login shell openclaw --version: {loginShell.Stdout}");
        var expectedGatewayVersion =
            Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION");
        if (!string.IsNullOrWhiteSpace(expectedGatewayVersion))
            Assert.Contains(expectedGatewayVersion.Trim(), loginShell.Stdout, StringComparison.Ordinal);

        var systemPath = await _fixture.RunInWslAsync(
            "env -i HOME=/home/openclaw USER=openclaw PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin openclaw --version",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(systemPath, "openclaw --version on default system PATH");
        Console.WriteLine($"[E2E] system PATH openclaw --version: {systemPath.Stdout}");
    }

    private static JsonElement FindWindowsNode(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("Platform", out var platform) &&
                platform.GetString()?.Contains("windows", StringComparison.OrdinalIgnoreCase) == true)
            {
                return node;
            }
        }

        return nodes[0];
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        return element.EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AssertCommandSucceeded(OpenClaw.SetupEngine.CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static JsonElement ReadNodeInvokePayload(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            return payload.Clone();
        }

        if (root.TryGetProperty("payloadJSON", out var payloadJson) &&
            payloadJson.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(payloadJson.GetString()))
        {
            using var doc = JsonDocument.Parse(payloadJson.GetString()!);
            return doc.RootElement.Clone();
        }

        throw new InvalidDataException($"Gateway node.invoke response did not include a payload object: {root.GetRawText()}");
    }

    private async Task SetGatewayAllowCommandsAsync(
        string configPath,
        IReadOnlyList<string> commands,
        IReadOnlyDictionary<string, string> environment)
    {
        string json = JsonSerializer.Serialize(commands);
        var result = await _fixture.RunInWslAsync(
            $"openclaw config set {configPath} {ShellSingleQuote(json)} --strict-json",
            TimeSpan.FromSeconds(60),
            environment,
            inputViaStdin: true);
        AssertCommandSucceeded(result, $"set {configPath} for Ollama proof");
        var restart = await _fixture.RunInWslAsync(
            "openclaw gateway restart || (systemctl --user restart openclaw-gateway.service && echo restarted-via-systemctl)",
            TimeSpan.FromSeconds(60),
            environment);
        AssertCommandSucceeded(restart, "restart gateway after Ollama allowlist change");
        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
    }

    private async Task SetOllamaPermissionAsync(bool enabled)
    {
        using var result = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.settings.set",
            new
            {
                name = nameof(SettingsData.NodeOllamaInferenceEnabled),
                value = enabled ? "true" : "false",
            });
        Assert.Equal(enabled, result.RootElement.GetProperty("value").GetBoolean());
    }

    private async Task ReconnectNodeForOllamaPermissionAsync()
    {
        using var reconnect =
            await _fixture.Client!.CallToolExpectSuccessAsync("app.connection.reconnectNode");
        Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
    }

    private async Task<OpenClaw.SetupEngine.CommandResult> InvokeOllamaChatThroughGatewayAsync(
        string nodeId,
        string model,
        IReadOnlyDictionary<string, string> environment)
    {
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = OllamaCapability.ChatCommand,
            @params = new
            {
                model,
                prompt = FakeOllamaServer.ExpectedPrompt,
                maxTokens = 32,
                temperature = 0,
                timeoutMs = 120_000,
            },
            timeoutMs = 130_000,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        return await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout 140000",
            TimeSpan.FromSeconds(150),
            environment,
            inputViaStdin: true);
    }

    private async Task WaitForNodeCommandAsync(
        string nodeId,
        string command,
        bool expectedPresent,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastResponse = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.nodes");
            lastResponse = doc.RootElement.GetRawText();
            JsonElement node = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault(candidate =>
                    string.Equals(
                        ReadNonEmptyStringProperty(candidate, "NodeId"),
                        nodeId,
                        StringComparison.OrdinalIgnoreCase))
                : default;
            JsonElement commands = default;
            bool nodeReady = node.ValueKind == JsonValueKind.Object &&
                             node.TryGetProperty("IsOnline", out var online) &&
                             online.GetBoolean() &&
                             node.TryGetProperty("Commands", out commands);
            string[] effectiveCommands = nodeReady ? ReadStringArray(commands).ToArray() : [];
            bool present = effectiveCommands.Contains(command, StringComparer.Ordinal);
            bool baselinePresent = effectiveCommands.Contains("system.which", StringComparer.Ordinal);
            if (nodeReady && baselinePresent && present == expectedPresent)
                return;
            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Node command '{command}' did not reach expected presence={expectedPresent}. " +
            $"Last app.nodes response: {lastResponse}");
    }

    private async Task ApproveNodeCommandUntilEffectiveAsync(
        string nodeId,
        string command,
        TimeSpan timeout)
    {
        var approvedRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastNodes = "<none>";
        string lastApprovals = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using (var approvals = await ReadPendingApprovalsFromConnectionPageAsync())
            {
                lastApprovals = approvals.RootElement.GetRawText();
                bool approvedAny = false;
                foreach (var request in ReadPendingNodeApprovals(approvals.RootElement)
                             .Where(request =>
                                 string.Equals(request.NodeId, nodeId, StringComparison.OrdinalIgnoreCase) &&
                                 approvedRequestIds.Add(request.RequestId)))
                {
                    using var approve =
                        await ApproveNodePairingFromConnectionPageAsync(request.RequestId);
                    Console.WriteLine(
                        "[E2E] approved pending Ollama node command trust request.");
                    approvedAny = true;
                }

                if (approvedAny)
                {
                    await ReconnectNodeForOllamaPermissionAsync();
                    await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
                }
            }

            using var nodes = await _fixture.Client!.CallToolExpectSuccessAsync("app.nodes");
            lastNodes = nodes.RootElement.GetRawText();
            JsonElement node = nodes.RootElement.ValueKind == JsonValueKind.Array
                ? nodes.RootElement.EnumerateArray().FirstOrDefault(candidate =>
                    string.Equals(
                        ReadNonEmptyStringProperty(candidate, "NodeId"),
                        nodeId,
                        StringComparison.OrdinalIgnoreCase))
                : default;
            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("IsOnline", out var online) &&
                online.GetBoolean() &&
                node.TryGetProperty("Commands", out var commands) &&
                ReadStringArray(commands).Contains(command, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Node command '{command}' did not become approved and effective. " +
            $"Last approvals: {lastApprovals}. Last app.nodes response: {lastNodes}");
    }

    private int CountTrayNodeInvocations(string command)
    {
        string path = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
        if (!File.Exists(path))
            return 0;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        int count = 0;
        string marker = $"[NODE] Invoking command: {command}";
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static string ShellSingleQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void AssertReadyStatus(JsonElement root)
    {
        var rawJson = root.GetRawText();
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");
        Assert.True(root.GetProperty("nodeConnected").GetBoolean(), $"nodeConnected should be true; full status: {rawJson}");
        Assert.True(root.GetProperty("nodePaired").GetBoolean(), $"nodePaired should be true; full status: {rawJson}");
    }

    private static void AssertOperatorCanApproveNodeTrust(JsonElement root)
    {
        var rawJson = root.GetRawText();
        Assert.True(root.TryGetProperty("operatorScopes", out var scopes), $"operatorScopes missing from app.status: {rawJson}");
        var values = ReadStringArray(scopes);
        Assert.Contains(values, scope => string.Equals(scope, "operator.admin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(values, scope => string.Equals(scope, "operator.pairing", StringComparison.OrdinalIgnoreCase));
    }

    private async Task AssertGatewayCliStateHealthy()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var devices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(devices, "list gateway devices after reconnect");
        AssertNoPendingRequests(devices.Stdout);

        var nodes = await _fixture.RunInWslAsync("openclaw nodes list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(nodes, "list gateway nodes after reconnect");
        AssertNoPendingRequests(nodes.Stdout);
        Assert.Contains("windows", nodes.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertPrimaryTrayReadyAndGatewayCliHealthyAsync()
    {
        await _fixture.WaitForConnectionReady();
        await _fixture.WaitForNodeListReady();
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        if (await ApprovePendingNodeTrustRequestsForHealthyStateAsync(nodeId))
            await ReconnectPrimaryNodeAndWaitForEffectiveStateAsync(nodeId);

        await AssertGatewayCliStateHealthy();
        using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        AssertReadyStatus(statusDoc.RootElement);
        AssertOperatorCanApproveNodeTrust(statusDoc.RootElement);
    }

    private async Task ReconnectPrimaryNodeAndWaitForEffectiveStateAsync(string nodeId)
    {
        using var reconnectNode = await _fixture.Client!.CallToolExpectSuccessAsync("app.connection.reconnectNode");
        Assert.True(reconnectNode.RootElement.GetProperty("reconnected").GetBoolean());
        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await WaitForNodeEffectiveStateAsync(
            _fixture.Client!,
            nodeId,
            new CapabilitiesConfig(),
            TimeSpan.FromSeconds(90));
    }

    private async Task<bool> ApprovePendingNodeTrustRequestsForHealthyStateAsync(string nodeId)
    {
        var approvedRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        DateTime? quietSince = null;
        string lastOutput = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
            lastOutput = approvals.RootElement.GetRawText();
            var requests = ReadPendingNodeApprovals(approvals.RootElement)
                .Where(request => string.Equals(request.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var request in requests.Where(request => approvedRequestIds.Add(request.RequestId)))
            {
                using var approve = await ApproveNodePairingFromConnectionPageAsync(request.RequestId);
                Console.WriteLine($"[E2E] explicitly approved pending node-trust request via Connection page: {approve.RootElement.GetRawText()}");
            }

            if (requests.Length > 0)
            {
                quietSince = null;
            }
            else
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince >= TimeSpan.FromSeconds(3))
                    return approvedRequestIds.Count > 0;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for pending node approvals for {nodeId} to remain clear. Last output: {lastOutput}");
    }

    private async Task<string> MintRealGatewaySetupCodeAsync(Dictionary<string, string> env, string description)
    {
        var qr = await _fixture.RunInWslAsync("openclaw qr --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(qr, description);

        using var qrDoc = JsonDocument.Parse(ExtractJsonObject(qr.Stdout));
        var setupCode = qrDoc.RootElement.GetProperty("setupCode").GetString();
        Assert.False(string.IsNullOrWhiteSpace(setupCode));
        return setupCode!;
    }

    private static Dictionary<string, string> GatewayTokenEnv(string? sharedGatewayToken)
    {
        return new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = RequireSharedGatewayToken(sharedGatewayToken) };
    }

    private static string RequireSharedGatewayToken(string? sharedGatewayToken)
    {
        Assert.False(string.IsNullOrWhiteSpace(sharedGatewayToken));
        return sharedGatewayToken!;
    }

    private async Task<HashSet<string>> ReadPendingDeviceRequestIdsAsync()
    {
        using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
        return ReadPendingApprovalIds(approvals.RootElement, "devicePending", "RequestId", "DeviceId");
    }

    private async Task<HashSet<string>> ReadPendingNodeRequestIdsAsync()
    {
        using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
        return ReadPendingNodeApprovals(approvals.RootElement)
            .Select(request => request.RequestId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<PendingNodeApproval>> ReadNewPendingNodeApprovalsUntilAsync(
        HashSet<string> ignoredRequestIds,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
            var requests = ReadPendingNodeApprovals(approvals.RootElement)
                .Where(request => !ignoredRequestIds.Contains(request.RequestId))
                .ToArray();
            if (requests.Length > 0)
                return requests;

            await Task.Delay(500);
        }

        return Array.Empty<PendingNodeApproval>();
    }

    private async Task<string> WaitForFirstPendingDeviceRequestIdAsync(
        HashSet<string> ignoredRequestIds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string lastOutput = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
            lastOutput = approvals.RootElement.GetRawText();
            var requestId = ReadPendingApprovalIds(approvals.RootElement, "devicePending", "RequestId", "DeviceId")
                .FirstOrDefault(id => !ignoredRequestIds.Contains(id));
            if (!string.IsNullOrWhiteSpace(requestId))
                return requestId;

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for pending device approval. Last output: {lastOutput}");
    }

    private async Task ApproveNewPendingDeviceRequestsUntilReadyAsync(
        HashSet<string> ignoredRequestIds,
        HashSet<string> approvedRequestIds,
        IsolatedTrayInstance tray)
    {
        var approved = new HashSet<string>(ignoredRequestIds, StringComparer.Ordinal);
        foreach (var requestId in approvedRequestIds)
            approved.Add(requestId);

        var deadline = DateTime.UtcNow.AddSeconds(90);
        string lastDevicesOutput = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            var credentials = tray.ReadCredentialState();
            if (credentials.HasOperatorToken && credentials.HasNodeToken && !credentials.HasBootstrapToken)
                return;

            if (credentials.HasOperatorToken && !credentials.HasNodeToken && !credentials.HasBootstrapToken)
            {
                using var reconnectNodeDoc = await tray.Client.CallToolExpectSuccessAsync("app.connection.reconnectNode");
                Assert.True(reconnectNodeDoc.RootElement.GetProperty("reconnected").GetBoolean());
                await Task.Delay(500);
                continue;
            }

            using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
            lastDevicesOutput = approvals.RootElement.GetRawText();
            var approvedAny = false;
            foreach (var requestId in ReadPendingApprovalIds(approvals.RootElement, "devicePending", "RequestId", "DeviceId")
                         .Where(id => approved.Add(id))
                         .ToArray())
            {
                using var approve = await ApproveDevicePairingFromConnectionPageAsync(requestId);
                Console.WriteLine($"[E2E] approved external-like device request via Connection page: {approve.RootElement.GetRawText()}");
                approvedRequestIds.Add(requestId);
                approvedAny = true;
            }

            if (approvedAny)
            {
                var updatedCredentials = tray.ReadCredentialState();
                if (!updatedCredentials.HasOperatorToken)
                {
                    using var reconnectDoc = await tray.Client.CallToolExpectSuccessAsync("app.connection.reconnect");
                    Assert.True(reconnectDoc.RootElement.GetProperty("reconnected").GetBoolean());
                }
                else if (!updatedCredentials.HasNodeToken)
                {
                    using var reconnectNodeDoc = await tray.Client.CallToolExpectSuccessAsync("app.connection.reconnectNode");
                    Assert.True(reconnectNodeDoc.RootElement.GetProperty("reconnected").GetBoolean());
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for clean external-like tray credentials. Last devices list: {lastDevicesOutput}");
    }

    private async Task RejectNewPendingApprovalsUntilQuietAsync(
        HashSet<string> deviceRequestIdsBefore,
        HashSet<string> nodeRequestIdsBefore,
        HashSet<string> handledDeviceRequestIds,
        HashSet<string> handledNodeRequestIds,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        DateTime? quietSince = null;
        string lastOutput = "<none>";

        while (DateTime.UtcNow < deadline)
        {
            using var approvals = await ReadPendingApprovalsFromConnectionPageAsync();
            lastOutput = approvals.RootElement.GetRawText();
            var rejectedAny = false;
            var handledRequestStillVisible = false;

            foreach (var requestId in ReadPendingApprovalIds(approvals.RootElement, "devicePending", "RequestId", "DeviceId")
                         .ToArray())
            {
                if (deviceRequestIdsBefore.Contains(requestId))
                    continue;

                if (handledDeviceRequestIds.Contains(requestId))
                {
                    handledRequestStillVisible = true;
                    continue;
                }

                using var reject = await RejectDevicePairingFromConnectionPageAsync(requestId);
                handledDeviceRequestIds.Add(requestId);
                Console.WriteLine($"[E2E] cleaned up pending device request {requestId}: {reject.RootElement.GetRawText()}");
                rejectedAny = true;
            }

            foreach (var request in ReadPendingNodeApprovals(approvals.RootElement)
                         .ToArray())
            {
                if (nodeRequestIdsBefore.Contains(request.RequestId))
                    continue;

                if (handledNodeRequestIds.Contains(request.RequestId))
                {
                    handledRequestStillVisible = true;
                    continue;
                }

                using var reject = await RejectNodePairingFromConnectionPageAsync(request.RequestId);
                handledNodeRequestIds.Add(request.RequestId);
                Console.WriteLine($"[E2E] cleaned up pending node-trust request {request.RequestId} for {request.NodeId}: {reject.RootElement.GetRawText()}");
                rejectedAny = true;
            }

            if (rejectedAny || handledRequestStillVisible)
            {
                quietSince = null;
            }
            else
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince >= TimeSpan.FromSeconds(3))
                    return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for external-tray pending approvals to remain clear. Last output: {lastOutput}");
    }

    private async Task<JsonDocument> ReadPendingApprovalsFromConnectionPageAsync()
    {
        await NavigateAdminTrayToConnectionPageAsync();

        return await _fixture.Client!.CallToolExpectSuccessAsync("app.connection.pendingApprovals");
    }

    private async Task<JsonDocument> ApproveDevicePairingFromConnectionPageAsync(string requestId)
    {
        await NavigateAdminTrayToConnectionPageAsync();

        var doc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.approveDevicePairing",
            new { requestId });
        AssertConnectionPageDecisionSucceeded(doc.RootElement, "device", "approve", requestId);
        return doc;
    }

    private async Task<JsonDocument> RejectDevicePairingFromConnectionPageAsync(string requestId)
    {
        await NavigateAdminTrayToConnectionPageAsync();

        var doc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.rejectDevicePairing",
            new { requestId });
        AssertConnectionPageDecisionSucceeded(doc.RootElement, "device", "reject", requestId);
        return doc;
    }

    private async Task<JsonDocument> ApproveNodePairingFromConnectionPageAsync(string requestId)
    {
        await NavigateAdminTrayToConnectionPageAsync();

        var doc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.approveNodePairing",
            new { requestId });
        AssertConnectionPageDecisionSucceeded(doc.RootElement, "node", "approve", requestId);
        return doc;
    }

    private async Task<JsonDocument> RejectNodePairingFromConnectionPageAsync(string requestId)
    {
        await NavigateAdminTrayToConnectionPageAsync();

        var doc = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.connection.rejectNodePairing",
            new { requestId });
        AssertConnectionPageDecisionSucceeded(doc.RootElement, "node", "reject", requestId);
        return doc;
    }

    private async Task NavigateAdminTrayToConnectionPageAsync()
    {
        using var navigate = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.navigate",
            new { page = "connection" });
        Assert.True(navigate.RootElement.GetProperty("navigated").GetBoolean(), $"Expected admin tray to navigate to Connection page: {navigate.RootElement.GetRawText()}");
    }

    private static void AssertExternalTrayDurablePairing(IsolatedTrayInstance tray)
    {
        var credentials = tray.ReadCredentialState();
        Assert.True(credentials.HasOperatorToken, "Expected isolated tray operator token after approval recovery.");
        Assert.True(credentials.HasNodeToken, "Expected isolated tray node token after approval recovery.");
        Assert.False(credentials.HasBootstrapToken, "Bootstrap token should be cleared after isolated tray role tokens are durable.");
    }

    private static void AssertConnectionPageDecisionSucceeded(JsonElement root, string kind, string action, string requestId)
    {
        Assert.True(root.GetProperty("connected").GetBoolean(), $"Admin tray should stay connected while deciding pairing request: {root.GetRawText()}");
        Assert.True(root.TryGetProperty("decision", out var decision) && decision.ValueKind == JsonValueKind.Object,
            $"Pairing decision response should include a decision object: {root.GetRawText()}");
        Assert.Equal(kind, decision.GetProperty("kind").GetString());
        Assert.Equal(action, decision.GetProperty("action").GetString());
        Assert.Equal(requestId, decision.GetProperty("requestId").GetString());
        Assert.True(decision.GetProperty("succeeded").GetBoolean(),
            $"Connection page {action} action should succeed for {kind} request {requestId}: {root.GetRawText()}");
    }

    private static HashSet<string> ReadPendingApprovalIds(JsonElement root, string arrayProperty, string requestIdProperty, string fallbackIdProperty)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(arrayProperty, out var pending) ||
            pending.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var request in pending.EnumerateArray())
        {
            var requestId = ReadNonEmptyStringProperty(request, requestIdProperty);
            var fallbackId = ReadNonEmptyStringProperty(request, fallbackIdProperty);
            var id = requestId ?? fallbackId;
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlyList<PendingNodeApproval> ReadPendingNodeApprovals(JsonElement root)
    {
        if (!root.TryGetProperty("nodePending", out var pending) ||
            pending.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PendingNodeApproval>();
        }

        return pending.EnumerateArray()
            .Select(request => new PendingNodeApproval(
                ReadNonEmptyStringProperty(request, "RequestId") ?? "",
                ReadNonEmptyStringProperty(request, "NodeId") ?? ""))
            .Where(request => request.RequestId.Length > 0 && request.NodeId.Length > 0)
            .ToArray();
    }

    private static async Task WaitForNodeEffectiveStateAsync(
        McpClient client,
        string nodeId,
        CapabilitiesConfig expected,
        TimeSpan timeout)
    {
        var expectedCapabilities = expected
            .GetEnabledCapabilities()
            .Select(capability => capability.Category)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedCommands = expected
            .GetEnabledCommandIds()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastResponse = "<none>";

        while (DateTime.UtcNow < deadline)
        {
            using var doc = await client.CallToolExpectSuccessAsync("app.nodes");
            var root = doc.RootElement;
            lastResponse = root.GetRawText();
            var node = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().FirstOrDefault(candidate =>
                    string.Equals(
                        ReadNonEmptyStringProperty(candidate, "NodeId"),
                        nodeId,
                        StringComparison.OrdinalIgnoreCase))
                : default;
            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("IsOnline", out var online) &&
                online.GetBoolean() &&
                node.TryGetProperty("Capabilities", out var capabilities) &&
                node.TryGetProperty("Commands", out var commands) &&
                expectedCapabilities.SequenceEqual(
                    ReadStringArray(capabilities).Order(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase) &&
                expectedCommands.SequenceEqual(
                    ReadStringArray(commands).Order(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Node {nodeId} did not reconnect with its approved effective capabilities and commands. Last app.nodes response: {lastResponse}");
    }

    private static string? ReadNonEmptyStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString();
        }

        var camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(camelCase, out property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString();
        }

        return null;
    }

    private static void AssertNoPendingRequests(string output)
    {
        using var doc = JsonDocument.Parse(ExtractJsonObject(output));
        if (doc.RootElement.TryGetProperty("pending", out var pending))
        {
            Assert.Equal(JsonValueKind.Array, pending.ValueKind);
            Assert.Equal(0, pending.GetArrayLength());
        }
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"Expected JSON object in output: {output}");
        return output[start..(end + 1)];
    }

    private sealed record PendingNodeApproval(string RequestId, string NodeId);

    private static void AssertJsonPath(JsonElement root, string[] path, string expected)
    {
        var value = GetJsonPath(root, path);
        var actual = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
        Assert.Equal(expected, actual);
    }

    private static JsonElement GetJsonPath(JsonElement root, string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            Assert.Equal(JsonValueKind.Object, current.ValueKind);
            JsonElement? next = null;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = property.Value;
                    break;
                }
            }
            Assert.True(next.HasValue, $"Expected JSON path {string.Join(".", path)}");
            current = next.Value;
        }
        return current;
    }

    private static string[] ParseJsonArrayFromOutput(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        Assert.True(start >= 0 && end > start, $"Expected JSON array in output: {output}");
        using var doc = JsonDocument.Parse(output[start..(end + 1)]);
        return ReadStringArray(doc.RootElement);
    }
}

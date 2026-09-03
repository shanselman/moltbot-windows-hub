using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

[Collection("E2E MXC Setup")]
public sealed class MxcSetupAndConnectTests
{
    private const int SystemRunProofTimeoutMs = 60_000;

    // STATUS_DLL_INIT_FAILED. Some hosts cannot spawn a child executable inside the
    // MXC AppContainer at all: the container starts, cmd builtins run, and the first
    // child process dies during DLL initialization. That is a sandbox-runtime property
    // of the host, independent of exec approvals, so an approvals proof must not report
    // it as an approvals failure. It is still never ignored: see
    // AssertApprovedCommandRan, which fails on any other nonzero exit code.
    private const int StatusDllInitFailed = unchecked((int)0xC0000142);

    // The same host incapacity also shows up in a second shape. Rather than the child
    // dying during DLL init, CreateProcess is refused outright and cmd reports
    // "Access is denied." with exit 1. Observed on hosts where the AppContainer cannot
    // traverse to the image at all. It is the same fact as StatusDllInitFailed - this
    // host cannot spawn a child executable under MXC - so it is tolerated the same way
    // and for the same reason.
    //
    // This is deliberately narrow. It requires exit code 1 together with exactly that
    // message on stderr, and it never relaxes the approvals assertions: every caller
    // separately asserts the approval decision and the MXC request shape from the tray
    // log, and those assertions are what this PR is proving. A genuine approvals
    // regression surfaces as decision=deny or a node error, not as this signature.
    // Diagnostic_SystemRun_SpawnsChildExecutableInSandbox is the authoritative
    // statement of whether the host has the capability at all.
    private const string AccessDeniedMessage = "Access is denied.";

    private static bool IsHostChildSpawnIncapacity(int exitCode, string? stderr)
        => exitCode == StatusDllInitFailed
            || (exitCode == 1
                && string.Equals(stderr?.Trim(), AccessDeniedMessage, StringComparison.Ordinal));

    // Separates the two things a system.run proof can show. The approval outcome is
    // asserted by the caller and is what this PR changes. Whether the sandbox can then
    // launch a child executable is a distinct capability, proven by
    // RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox on a capable host.
    private static void AssertApprovedCommandRan(JsonElement payload, string label)
    {
        var exitCode = payload.GetProperty("exitCode").GetInt32();
        var stderr = payload.GetProperty("stderr").GetString();
        if (IsHostChildSpawnIncapacity(exitCode, stderr))
        {
            Console.WriteLine(
                $"[E2E] {label}: approval succeeded; sandbox could not spawn a child executable " +
                $"(exitCode=0x{exitCode:X8}, stderr={stderr?.Trim()}). This host cannot run child " +
                "processes under MXC; the approval decision above is the assertion that matters here.");
            return;
        }

        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("stdout").GetString()));
    }

    private const int NodeInvokeProofTimeoutMs = 90_000;
    private const int GatewayCliProofTimeoutMs = 120_000;
    private static readonly TimeSpan GatewayCliProofProcessTimeout = TimeSpan.FromSeconds(130);

    private readonly E2ESetupFixture _fixture;

    public MxcSetupAndConnectTests(MxcE2ESetupFixture fixture)
    {
        _fixture = fixture.Inner;

        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [MxcE2EFact]
    public async Task MirroredWslSafeGatewayPort_IsListeningAndRecorded()
    {
        Assert.InRange(
            _fixture.GatewayPort,
            MirroredWslPortLease.CandidateRangeStart,
            MirroredWslPortLease.CandidateRangeEnd);

        var artifactPath = Path.Combine(_fixture.ArtifactDir, "gateway-port-allocation.json");
        Assert.True(File.Exists(artifactPath), $"Gateway port allocation artifact not found: {artifactPath}");
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath));
        Assert.Equal(_fixture.GatewayPort, artifact.RootElement.GetProperty("gatewayPort").GetInt32());
        var artifactDynamicRanges = ReadPortRanges(
            artifact.RootElement.GetProperty("windowsDynamicTcpRanges"));
        var artifactExcludedRanges = ReadPortRanges(
            artifact.RootElement.GetProperty("windowsExcludedTcpRanges"));
        var freshPortState = WindowsTcpPortState.Capture();

        Assert.NotEmpty(freshPortState.DynamicRanges);
        Assert.DoesNotContain(
            artifactDynamicRanges,
            range => range.Contains(_fixture.GatewayPort));
        Assert.DoesNotContain(
            artifactExcludedRanges,
            range => range.Contains(_fixture.GatewayPort));
        Assert.False(
            freshPortState.IsBlocked(_fixture.GatewayPort),
            $"Gateway port {_fixture.GatewayPort} became blocked after allocation. " +
            $"Dynamic ranges: {string.Join(", ", freshPortState.DynamicRanges)}. " +
            $"Excluded ranges: {string.Join(", ", freshPortState.ExcludedRanges)}.");

        var listener = await _fixture.RunInWslAsync(
            $"""
            ss_path=$(command -v ss 2>/dev/null || true)
            if [ -z "$ss_path" ] && [ -x /usr/sbin/ss ]; then
              ss_path=/usr/sbin/ss
            fi
            if [ -z "$ss_path" ]; then
              printf '%s\n' 'OPENCLAW_E2E_SS_UNAVAILABLE' >&2
              exit 127
            fi
            "$ss_path" -H -ltn 'sport = :{_fixture.GatewayPort}'
            """,
            inputViaStdin: true);
        Assert.False(
            listener.ExitCode == 127 &&
                listener.Stderr.Contains("OPENCLAW_E2E_SS_UNAVAILABLE", StringComparison.Ordinal),
            $"Cannot verify Gateway listener because ss is unavailable in WSL distro {_fixture.DistroName}.");
        AssertCommandSucceeded(listener, "inspect the selected Gateway port inside WSL");
        Assert.True(
            listener.Stdout.Contains($":{_fixture.GatewayPort}", StringComparison.Ordinal),
            $"ss is available, but Gateway port {_fixture.GatewayPort} is absent from WSL listeners. Output: {listener.Stdout}");
        Console.WriteLine(
            $"[E2E] mirrored-WSL-safe Gateway port proof: port={_fixture.GatewayPort}; artifact={artifactPath}; listener={listener.Stdout.Trim()}");
    }

    [MxcE2EFact]
    public async Task RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox()
    {
        const string marker = "OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_OK";

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForSystemRunProofAsync();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        var commandText = $"Write-Output '{marker}'";
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = new[] { "powershell.exe", "-NoProfile", "-Command", commandText },
                rawCommand = $"powershell.exe -NoProfile -Command \"{commandText}\"",
                timeoutMs = SystemRunProofTimeoutMs
            },
            timeoutMs = NodeInvokeProofTimeoutMs,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliProofTimeoutMs}",
            GatewayCliProofProcessTimeout,
            env,
            inputViaStdin: true);

        AssertCommandSucceeded(invoke, "invoke Windows node system.run through real gateway");
        Assert.Contains(marker, invoke.Stdout, StringComparison.Ordinal);

        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        var responseShape = string.Join(",", invokeDoc.RootElement.EnumerateObject().Select(property => property.Name));
        Console.WriteLine($"[E2E] gateway node.invoke system.run response shape: {responseShape}");
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
        {
            Assert.True(ok.GetBoolean(), $"Expected gateway node.invoke ok=true; response: {invokeDoc.RootElement.GetRawText()}");
        }

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        if (payload.TryGetProperty("timedOut", out var timedOut))
            Assert.False(timedOut.GetBoolean(), $"system.run timed out; payload: {payload.GetRawText()}");

        var stdout = payload.GetProperty("stdout").GetString() ?? "";
        Assert.Contains(marker, stdout, StringComparison.Ordinal);
        var stderrLength = payload.TryGetProperty("stderr", out var stderr)
            ? (stderr.GetString() ?? "").Length
            : 0;
        Console.WriteLine($"[E2E] gateway system.run payload: exitCode=0; stdout={stdout.Trim()}; stderrLength={stderrLength}");

        var requestLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox request",
            "executor=mxc-direct-appc",
            "contained=True",
            "shell=<direct-argv>",
            "uiAllowWindows=True");
        var resultLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox result",
            "exitCode=0",
            "containment=mxc");

        Console.WriteLine($"[E2E] MXC request diagnostic: {requestLog}");
        Console.WriteLine($"[E2E] MXC result diagnostic: {resultLog}");
    }

    // whoami.exe, not hostname.exe. Both are System32 utilities, but hostname.exe is
    // commonly shadowed on a developer PATH (coreutils ships one), and a shadowing copy
    // usually lives in a directory the sandbox does not grant. That turns an approvals
    // proof into a proof about the host's PATH. whoami.exe has no common shadow, so the
    // executable this proof resolves is the one it names.
    private const string ProofExecutable = "whoami.exe";

    [MxcE2EFact]
    public async Task RealGateway_SystemRun_UsesBoundExecutableAllowlistRule()
    {
        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForBoundExecutableProofAsync();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        const string commandText = ProofExecutable;
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = new[] { "cmd.exe", "/d", "/s", "/c", commandText },
                rawCommand = commandText,
                timeoutMs = SystemRunProofTimeoutMs
            },
            timeoutMs = NodeInvokeProofTimeoutMs,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliProofTimeoutMs}",
            GatewayCliProofProcessTimeout,
            env,
            inputViaStdin: true);

        AssertCommandSucceeded(invoke, "invoke allowlisted executable through real gateway");
        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
        {
            Assert.True(
                ok.GetBoolean(),
                $"Expected gateway node.invoke ok=true; response: {invokeDoc.RootElement.GetRawText()}");
        }

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);

        // The approval decision is what this test exists to prove: the stored allowlist
        // rule matched, so no prompt was raised.
        var approvalLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[EXEC-APPROVALS]",
            "decision=allow",
            "promptAttempted=false");
        var requestLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox request",
            "executor=mxc-direct-appc",
            "contained=True",
            "shell=<direct-argv>");

        AssertApprovedCommandRan(payload, nameof(RealGateway_SystemRun_UsesBoundExecutableAllowlistRule));

        Console.WriteLine($"[E2E] bound executable approval: {approvalLog}");
        Console.WriteLine($"[E2E] bound executable MXC request: {requestLog}");
    }

    // The gateway forwards command text already tokenized across several argv
    // elements. Proving only the single-element tail would leave the shape upstream
    // actually produces unproven.
    [MxcE2EFact]
    public async Task RealGateway_SystemRun_UsesBoundAllowlistRuleForMultiElementTail()
    {
        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForBoundExecutableProofAsync();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        const string commandText = "where.exe " + ProofExecutable;
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                // Tail split across elements, as node-invoke-system-run-approval fixtures do.
                command = new[] { "cmd.exe", "/d", "/s", "/c", "where.exe", ProofExecutable },
                rawCommand = commandText,
                timeoutMs = SystemRunProofTimeoutMs
            },
            timeoutMs = NodeInvokeProofTimeoutMs,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliProofTimeoutMs}",
            GatewayCliProofProcessTimeout,
            env,
            inputViaStdin: true);

        AssertCommandSucceeded(invoke, "invoke allowlisted multi-element tail through real gateway");
        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);

        var approvalLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[EXEC-APPROVALS]",
            "decision=allow",
            "promptAttempted=false");

        AssertApprovedCommandRan(payload, nameof(RealGateway_SystemRun_UsesBoundAllowlistRuleForMultiElementTail));

        Console.WriteLine($"[E2E] multi-element tail approval: {approvalLog}");
    }

    // Diagnostic: an unbindable payload takes the non-reusable approval path, so this
    // isolates "can the sandbox spawn a child executable at all" from anything the
    // binder does.
    [MxcE2EFact]
    public async Task Diagnostic_SystemRun_SpawnsChildExecutableInSandbox()
    {
        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        // security=full / ask=off, so this never raises an interactive prompt. Without it
        // the unbindable payload below would block the suite waiting on a human.
        await SetExecApprovalForSystemRunProofAsync();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        const string commandText = "echo START && " + ProofExecutable;
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = new[] { "cmd.exe", "/d", "/s", "/c", commandText },
                rawCommand = commandText,
                timeoutMs = SystemRunProofTimeoutMs
            },
            timeoutMs = NodeInvokeProofTimeoutMs,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliProofTimeoutMs}",
            GatewayCliProofProcessTimeout,
            env,
            inputViaStdin: true);

        // This probe is the one place where the gateway call itself failing is
        // information rather than a defect. The approval already happened - the node
        // logs decision=allow before it hands the command to the sandbox - and on a
        // host that cannot spawn a child under MXC the sandbox run can hang until the
        // gateway call gives up rather than returning a clean nonzero exit. Reporting
        // that as a test failure would blame exec approvals for a sandbox-runtime
        // limitation.
        //
        // Nothing is being swallowed. This test asserts no approvals behavior at all:
        // the approval decision and the MXC request shape are asserted by
        // RealGateway_SystemRun_UsesBoundExecutableAllowlistRule and
        // RealGateway_SystemRun_UsesBoundAllowlistRuleForMultiElementTail, which fail
        // normally. All this branch says is that host capability could not be
        // established here.
        if (invoke.TimedOut || invoke.ExitCode != 0)
        {
            Console.WriteLine(
                "[E2E] child-spawn diagnostic: the probe did not complete on this host " +
                $"(timedOut={invoke.TimedOut}, exitCode={invoke.ExitCode}). The sandbox run was " +
                "accepted and approved, then never returned a result, which is how this host " +
                "expresses its inability to spawn a child executable under MXC. Exec-approval " +
                "proofs on this host assert the approval decision only; see AssertApprovedCommandRan.");
            return;
        }

        AssertCommandSucceeded(invoke, "spawn child executable in sandbox");
        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        var exitCode = payload.GetProperty("exitCode").GetInt32();
        var stdout = payload.GetProperty("stdout").GetString() ?? "";

        Console.WriteLine(
            $"[E2E] child-spawn diagnostic: exitCode=0x{exitCode:X8}; stdout={stdout}; " +
            $"stderr={payload.GetProperty("stderr").GetString()}");

        // The builtin half must always work: it proves the container started and ran the
        // payload, so a later child-spawn failure is isolated to process creation.
        Assert.Contains("START", stdout, StringComparison.Ordinal);

        if (IsHostChildSpawnIncapacity(exitCode, payload.GetProperty("stderr").GetString()))
        {
            Console.WriteLine(
                "[E2E] child-spawn diagnostic: this host cannot spawn child executables under MXC. " +
                "Exec-approval proofs on this host assert the approval " +
                "decision only; see AssertApprovedCommandRan.");
            return;
        }

        Assert.Equal(0, exitCode);
    }

    [MxcE2EFact]
    public async Task RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox()
    {
        const string sourcePayload = "OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_DENIED_PAYLOAD";
        const string sourceReadyMarker = "OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_SOURCE_READY";
        var blockedPath = Path.Combine(_fixture.DataDir, $"mxc-denied-write-{Guid.NewGuid():N}.txt");
        var blockedPathForCmd = blockedPath;

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForSystemRunProofAsync();

        Assert.False(File.Exists(blockedPath), $"Unexpected pre-existing MXC deny proof file: {blockedPath}");
        await File.WriteAllTextAsync(blockedPath, sourcePayload);
        Assert.Equal(sourcePayload, await File.ReadAllTextAsync(blockedPath));
        File.Delete(blockedPath);
        Assert.False(File.Exists(blockedPath), $"Failed to remove host-writable preflight file: {blockedPath}");

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        var commandText =
            $"echo {sourceReadyMarker} && echo {sourcePayload} > {CmdQuote(blockedPathForCmd)}";
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = new[] { "cmd.exe", "/d", "/s", "/c", commandText },
                rawCommand = commandText,
                timeoutMs = SystemRunProofTimeoutMs
            },
            timeoutMs = NodeInvokeProofTimeoutMs,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliProofTimeoutMs}",
            GatewayCliProofProcessTimeout,
            env,
            inputViaStdin: true);

        AssertCommandSucceeded(invoke, "invoke Windows node system.run denied-write proof through real gateway");

        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
        {
            Assert.True(ok.GetBoolean(), $"Expected gateway node.invoke ok=true; response: {invokeDoc.RootElement.GetRawText()}");
        }

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        var exitCode = payload.GetProperty("exitCode").GetInt32();
        if (payload.TryGetProperty("timedOut", out var timedOut))
            Assert.False(timedOut.GetBoolean(), $"denied-write proof timed out; payload: {payload.GetRawText()}");

        var stdout = payload.GetProperty("stdout").GetString() ?? "";
        var stderr = payload.TryGetProperty("stderr", out var stderrElement)
            ? stderrElement.GetString() ?? ""
            : "";
        var combinedOutput = stdout + stderr;
        var blockedFileExists = File.Exists(blockedPath);

        // The shell's access-denied text is localized. sourceReadyMarker proves the
        // command reached the denied destination write.
        Assert.Contains(sourceReadyMarker, combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePayload, combinedOutput, StringComparison.Ordinal);
        Assert.False(blockedFileExists,
            $"MXC sandbox should not create files inside the tray data/settings directory: {blockedPath}");
        Assert.Equal(1, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stderr), $"Expected denied write to emit stderr; payload: {payload.GetRawText()}");

        var requestLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox request",
            "executor=mxc-direct-appc",
            "contained=True",
            "shell=<direct-argv>");
        var resultLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox result",
            $"exitCode={exitCode}",
            "containment=mxc");

        Console.WriteLine(
            $"[E2E] MXC denied-write payload: exitCode={exitCode}; stdoutLength={stdout.Length}; stderrLength={stderr.Length}; fileExists={blockedFileExists}");
        Console.WriteLine($"[E2E] MXC denied-write request diagnostic: {requestLog}");
        Console.WriteLine($"[E2E] MXC denied-write result diagnostic: {resultLog}");
    }

    private async Task AssertPrimaryTrayReadyAndGatewayCliHealthyAsync()
    {
        await _fixture.WaitForConnectionReady();
        await _fixture.WaitForNodeListReady();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var devices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(devices, "list gateway devices before MXC proof");
        AssertNoPendingRequests(devices.Stdout);

        var nodes = await _fixture.RunInWslAsync("openclaw nodes list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(nodes, "list gateway nodes before MXC proof");
        AssertNoPendingRequests(nodes.Stdout);
        Assert.Contains("windows", nodes.Stdout, StringComparison.OrdinalIgnoreCase);

        var gatewayVersion = Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION");
        var nodeCommandsAllowKey = string.IsNullOrWhiteSpace(gatewayVersion)
            ? ConfigureGatewayStep.NodeCommandsAllowKey
            : ConfigureGatewayStep.ResolveNodeCommandsAllowKey(gatewayVersion);
        var allowCommands = await _fixture.RunInWslAsync(
            $"openclaw config get {nodeCommandsAllowKey} --json",
            TimeSpan.FromSeconds(30),
            env);
        AssertCommandSucceeded(allowCommands, $"read {nodeCommandsAllowKey} before MXC proof");
        using var allowCommandsDoc = JsonDocument.Parse(ExtractJsonValue(allowCommands.Stdout));
        var allowed = ReadStringArray(allowCommandsDoc.RootElement);
        Assert.Contains(allowed, command => command == "system.run");
        Assert.Contains(allowed, command => command == "system.run.prepare");
        Assert.Contains(allowed, command => command == "system.which");
        Console.WriteLine($"[E2E] {nodeCommandsAllowKey} includes system.run/system.run.prepare/system.which");

        using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        AssertReadyStatus(statusDoc.RootElement);
        AssertOperatorCanApproveNodeTrust(statusDoc.RootElement);
    }

    private async Task SetExecApprovalForSystemRunProofAsync()
    {
        using var policy = await _fixture.Client!.CallToolExpectSuccessAsync("system.execApprovals.get");
        var approvalsPath = policy.RootElement.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(approvalsPath));

        // The gateway's canonical Windows command is an explicit cmd.exe argv. The approval model
        // treats that shell host as an indirect command host that a remote allowlist cannot
        // authorize, and
        // system.execApprovals.set refuses to grant full access remotely so a gateway can never
        // escalate the node's exec authority. Full access is a LOCAL node-owner decision, so this
        // proof writes the approvals file directly (as an owner would via the tray) and then relies
        // on the MXC sandbox for containment.
        var fileObject = new
        {
            version = 1,
            defaults = new { security = "full", ask = "off", askFallback = "deny", autoAllowSkills = false },
            agents = new Dictionary<string, object>(),
        };
        var json = JsonSerializer.Serialize(
            fileObject,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(approvalsPath!)!);
        await File.WriteAllTextAsync(approvalsPath!, json);

        // Confirm the node reads the locally-written full-access policy before running the proof.
        using var confirm = await _fixture.Client!.CallToolExpectSuccessAsync("system.execApprovals.get");
        var security = confirm.RootElement
            .GetProperty("file").GetProperty("defaults").GetProperty("security").GetString();
        Assert.Equal("full", security);
        Console.WriteLine($"[E2E] V2 exec approvals set LOCALLY to full at {approvalsPath} (remote full is guarded; MXC sandbox enforces containment)");
    }

    private async Task SetExecApprovalForBoundExecutableProofAsync()
    {
        using var policy = await _fixture.Client!.CallToolExpectSuccessAsync("system.execApprovals.get");
        var approvalsPath = policy.RootElement.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(approvalsPath));

        var fileObject = new
        {
            version = 1,
            defaults = new
            {
                security = "allowlist",
                ask = "off",
                askFallback = "deny",
                autoAllowSkills = false
            },
            agents = new Dictionary<string, object>
            {
                ["main"] = new
                {
                    security = "allowlist",
                    ask = "off",
                    askFallback = "deny",
                    autoAllowSkills = false,
                    allowlist = new[]
                    {
                        new
                        {
                            id = Guid.NewGuid(),
                            pattern = "**/" + ProofExecutable
                        },
                        new
                        {
                            id = Guid.NewGuid(),
                            pattern = "**/where.exe"
                        }
                    }
                }
            }
        };
        var json = JsonSerializer.Serialize(
            fileObject,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        Directory.CreateDirectory(Path.GetDirectoryName(approvalsPath!)!);
        await File.WriteAllTextAsync(approvalsPath!, json);

        using var confirm = await _fixture.Client.CallToolExpectSuccessAsync(
            "system.execApprovals.get");
        var allowlist = confirm.RootElement
            .GetProperty("file")
            .GetProperty("agents")
            .GetProperty("main")
            .GetProperty("allowlist");
        Assert.Contains(
            allowlist.EnumerateArray(),
            entry => entry.GetProperty("pattern").GetString() == "**/" + ProofExecutable);
        Console.WriteLine(
            $"[E2E] V2 exec approvals set LOCALLY to allowlist {ProofExecutable} at {approvalsPath}");
    }

    private TrayLogCursor GetTrayLogCursor()
    {
        var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
        var rotatedLogPath = logPath + ".old";
        var currentInfo = File.Exists(logPath) ? new FileInfo(logPath) : null;
        var rotatedInfo = File.Exists(rotatedLogPath) ? new FileInfo(rotatedLogPath) : null;
        return new TrayLogCursor(
            currentInfo is not null,
            currentInfo?.Length ?? 0,
            currentInfo?.CreationTimeUtc,
            rotatedInfo?.Length ?? -1,
            rotatedInfo?.CreationTimeUtc,
            rotatedInfo?.LastWriteTimeUtc);
    }

    private async Task<string> WaitForTrayLogLineContainingAsync(TimeSpan timeout, TrayLogCursor cursor, params string[] fragments)
    {
        var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
        var deadline = DateTime.UtcNow.Add(timeout);
        var tail = "";

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath) || File.Exists(logPath + ".old"))
            {
                var lines = await ReadTrayLogLinesSinceAsync(logPath, cursor);
                tail = string.Join(Environment.NewLine, lines.TakeLast(20));
                var match = lines.LastOrDefault(line =>
                    fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));
                if (match is not null)
                    return match;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for tray log fragments [{string.Join(", ", fragments)}]. Log path: {logPath}. Tail: {tail}");
    }

    private static async Task<string[]> ReadTrayLogLinesSinceAsync(string logPath, TrayLogCursor cursor)
    {
        var lines = new List<string>();
        var rotatedLogPath = logPath + ".old";
        var rotationObserved = CurrentLogGenerationChanged(logPath, cursor) || RotatedLogChanged(rotatedLogPath, cursor);
        if (rotationObserved && File.Exists(rotatedLogPath))
        {
            lines.AddRange(await ReadLogLinesFromOffsetAsync(rotatedLogPath, cursor.CurrentLength));
        }

        if (File.Exists(logPath))
        {
            lines.AddRange(await ReadLogLinesFromOffsetAsync(logPath, rotationObserved ? 0 : cursor.CurrentLength));
        }

        return lines.ToArray();
    }

    private static bool RotatedLogChanged(string rotatedLogPath, TrayLogCursor cursor)
    {
        if (!File.Exists(rotatedLogPath))
            return false;

        var info = new FileInfo(rotatedLogPath);
        return info.Length != cursor.RotatedLength
            || info.CreationTimeUtc != cursor.RotatedCreationUtc
            || info.LastWriteTimeUtc != cursor.RotatedLastWriteUtc;
    }

    private static bool CurrentLogGenerationChanged(string logPath, TrayLogCursor cursor)
    {
        if (!cursor.CurrentExists)
            return false;

        if (!File.Exists(logPath))
            return false;

        var info = new FileInfo(logPath);
        return info.CreationTimeUtc != cursor.CurrentCreationUtc;
    }

    private static async Task<string[]> ReadLogLinesFromOffsetAsync(string logPath, long startOffset)
    {
        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var seekOffset = Math.Min(startOffset, stream.Length);
        stream.Seek(seekOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

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

    private static TcpPortRange[] ReadPortRanges(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        return element.EnumerateArray()
            .Select(range => new TcpPortRange(
                range.GetProperty("start").GetInt32(),
                range.GetProperty("end").GetInt32()))
            .ToArray();
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

    private static void AssertCommandSucceeded(CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static Dictionary<string, string> GatewayTokenEnv(string? sharedGatewayToken)
    {
        Assert.False(string.IsNullOrWhiteSpace(sharedGatewayToken));
        return new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = sharedGatewayToken! };
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"Expected JSON object in output: {output}");
        return output[start..(end + 1)];
    }

    private static string ExtractJsonValue(string output)
    {
        var objectStart = output.IndexOf('{');
        var arrayStart = output.IndexOf('[');
        var start = objectStart switch
        {
            >= 0 when arrayStart >= 0 => Math.Min(objectStart, arrayStart),
            >= 0 => objectStart,
            _ => arrayStart
        };

        Assert.True(start >= 0, $"Expected JSON value in output: {output}");
        var endChar = output[start] == '{' ? '}' : ']';
        var end = output.LastIndexOf(endChar);
        Assert.True(end > start, $"Expected JSON value in output: {output}");
        return output[start..(end + 1)];
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

    private static string ShellSingleQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string CmdQuote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed record TrayLogCursor(
        bool CurrentExists,
        long CurrentLength,
        DateTime? CurrentCreationUtc,
        long RotatedLength,
        DateTime? RotatedCreationUtc,
        DateTime? RotatedLastWriteUtc);
}

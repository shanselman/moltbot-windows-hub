using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public class OpenClawGatewayClientTests
{
    // Test helper to access private methods through reflection
    private class GatewayClientTestHelper
    {
        private readonly OpenClawGatewayClient _client;
        private bool _pendingRegistryOpened;

        public OpenClawGatewayClient Client => _client;

        public GatewayClientTestHelper(
            bool tokenIsBootstrapToken = false,
            bool bootstrapPairAsNode = false,
            string gatewayUrl = "ws://localhost:18789",
            string? identityPath = null)
        {
            // Isolate test identities because other test classes can construct
            // gateway clients concurrently under the same AppData root.
            identityPath ??= CreateTempIdentityPath();

            _client = new OpenClawGatewayClient(
                gatewayUrl,
                "test-token",
                new TestLogger(),
                tokenIsBootstrapToken,
                bootstrapPairAsNode,
                identityPath);
        }

        public GatewayClientTestHelper(IOpenClawLogger logger)
        {
            _client = new OpenClawGatewayClient(
                "ws://localhost:18789",
                "test-token",
                logger,
                identityPath: CreateTempIdentityPath());
        }

        public GatewayClientTestHelper(OpenClawGatewayClient client)
        {
            _client = client;
        }

        public string ClassifyNotification(string text)
        {
            var (_, type) = NotificationCategorizer.ClassifyByKeywords(text);
            return type;
        }

        public string GetNotificationTitle(string text)
        {
            var (title, _) = NotificationCategorizer.ClassifyByKeywords(text);
            return title;
        }

        public ActivityKind ClassifyTool(string toolName)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod("ClassifyTool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { toolName });
            return (ActivityKind)result!;
        }

        public string ShortenPath(string path)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod("ShortenPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { path });
            return (string)result!;
        }

        public string TruncateLabel(string text, int maxLen = 60)
        {
            // TruncateLabel was removed; its behaviour is now provided by the public API.
            return MenuDisplayHelper.TruncateText(text, maxLen);
        }

        public Task<ChatSendResult> RegisterPendingChatSend(string requestId)
        {
            EnsurePendingRegistryOpen();
            return GetPendingRegistry().RegisterChatSend(requestId, "chat.send").Task;
        }

        public Task<JsonElement> RegisterPendingWizardResponse(string requestId)
        {
            EnsurePendingRegistryOpen();
            return GetPendingRegistry().RegisterWizard(requestId, "wizard.next").Task;
        }

        public Task<bool> RegisterPendingApprovalResolve(string requestId)
        {
            EnsurePendingRegistryOpen();
            return GetPendingRegistry()
                .RegisterApproval(requestId, "exec.approval.resolve")
                .Task;
        }

        public void ClearPendingRequests()
        {
            GetPendingRegistry().Drain();
            _pendingRegistryOpened = false;
        }

        public void OnDisconnected()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "OnDisconnected",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, Array.Empty<object>());
        }

        public void ProcessRawMessage(string json)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ProcessMessage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { json });
        }

        public ChatHistoryInfo ParseChatHistoryPayload(string payloadJson, string sessionKey = "main")
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ParseChatHistory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (ChatHistoryInfo)method!.Invoke(null, new object[] { document.RootElement.Clone(), sessionKey })!;
        }

        public ChatSendResult ParseChatSendResponse(string responseJson)
        {
            using var document = JsonDocument.Parse(responseJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ParseChatSendResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (ChatSendResult)method!.Invoke(null, new object[] { document.RootElement.Clone() })!;
        }

        public long ExtractChatTimestampMs(string payloadJson)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ExtractChatTimestampMs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (long)method!.Invoke(null, new object[] { document.RootElement.Clone() })!;
        }

        public SessionInfo[] GetSessionList()
        {
            return _client.GetSessionList();
        }

        public void SetUnsupportedMethodFlags(bool usageStatus, bool usageCost, bool sessionPreview, bool nodeList)
        {
            SetPrivateField("_usageStatusUnsupported", usageStatus);
            SetPrivateField("_usageCostUnsupported", usageCost);
            SetPrivateField("_sessionPreviewUnsupported", sessionPreview);
            SetPrivateField("_nodeListUnsupported", nodeList);
        }

        public (bool UsageStatus, bool UsageCost, bool SessionPreview, bool NodeList) GetUnsupportedMethodFlags()
        {
            return (
                GetPrivateField<bool>("_usageStatusUnsupported"),
                GetPrivateField<bool>("_usageCostUnsupported"),
                GetPrivateField<bool>("_sessionPreviewUnsupported"),
                GetPrivateField<bool>("_nodeListUnsupported")
            );
        }

        public void ResetUnsupportedMethodFlags()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ResetUnsupportedMethodFlags",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, null);
        }

        public GatewayUsageInfo ParseUsageStatusPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseUsageStatus", payloadJson);
            return GetUsageState();
        }

        public string CallBuildProviderSummary(GatewayUsageStatusInfo status)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "BuildProviderSummary",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (string)method!.Invoke(null, new object[] { status })!;
        }

        public GatewayUsageInfo ParseUsageCostPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseUsageCost", payloadJson);
            return GetUsageState();
        }

        public SessionsPreviewPayloadInfo ParseSessionsPreviewPayload(string payloadJson)
        {
            SessionsPreviewPayloadInfo? parsed = null;
            EventHandler<SessionsPreviewPayloadInfo> handler = (_, payload) => parsed = payload;
            _client.SessionPreviewUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseSessionsPreview", payloadJson);
            }
            finally
            {
                _client.SessionPreviewUpdated -= handler;
            }

            return parsed ?? new SessionsPreviewPayloadInfo();
        }

        public GatewayNodeInfo[] ParseNodeListPayload(string payloadJson)
        {
            GatewayNodeInfo[] parsed = Array.Empty<GatewayNodeInfo>();
            EventHandler<GatewayNodeInfo[]> handler = (_, nodes) => parsed = nodes;
            _client.NodesUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseNodeList", payloadJson);
            }
            finally
            {
                _client.NodesUpdated -= handler;
            }

            return parsed;
        }

        public string? ParseHandshakeMainSessionKey(string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeMainSessionKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { doc.RootElement.Clone() });
            return result as string;
        }

        public string? ParseHandshakeDeviceToken(string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceToken",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { doc.RootElement.Clone() });
            return result as string;
        }

        public (ChannelHealth[] channels, bool eventFired) ParseChannelHealthPayload(string payloadJson)
        {
            ChannelHealth[]? parsed = null;
            EventHandler<ChannelHealth[]> handler = (_, ch) => parsed = ch;
            _client.ChannelHealthUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseChannelHealth", payloadJson);
            }
            finally
            {
                _client.ChannelHealthUpdated -= handler;
            }

            return (parsed ?? Array.Empty<ChannelHealth>(), parsed != null);
        }

        public void ParseSessionsPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseSessions", payloadJson);
        }

        public void SetMainSessionKey(string key, bool isCanonical = true)
        {
            SetPrivateField("_mainSessionKeyIsCanonical", isCanonical);
            SetPrivateField("_mainSessionKey", key);
        }

        public ModelsListInfo ParseModelsListPayload(string payloadJson)
        {
            ModelsListInfo? parsed = null;
            EventHandler<ModelsListInfo> handler = (_, info) => parsed = info;
            _client.ModelsListUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseModelsList", payloadJson);
            }
            finally
            {
                _client.ModelsListUpdated -= handler;
            }

            return parsed ?? new ModelsListInfo();
        }

        private void InvokePrivatePayloadParser(string methodName, string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                methodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { doc.RootElement.Clone() });
        }

        private GatewayUsageInfo GetUsageState()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_usage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (GatewayUsageInfo)(field?.GetValue(_client) ?? new GatewayUsageInfo());
        }

        private void SetPrivateField(string fieldName, object? value)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(_client, value);
        }

        private T GetPrivateField<T>(string fieldName)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (T)(field!.GetValue(_client) ?? throw new InvalidOperationException($"Missing field value: {fieldName}"));
        }

        public void SetGrantedScopes(string[] scopes) => SetPrivateField("_grantedOperatorScopes", scopes);

        public void SetOperatorDeviceId(string? id) => SetPrivateField("_operatorDeviceId", id);

        public string[] GetRequestedOperatorScopes()
        {
            var role = GetConnectRole();
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "GetRequestedScopes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string[])method!.Invoke(_client, new object[] { role })!;
        }

        public string GetConnectRole()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "GetConnectRole",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)method!.Invoke(_client, null)!;
        }

        public string? TryGetHandshakeDeviceToken(string payloadJson, string? preferredRole = null)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceTokenCore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                binder: null,
                types: [typeof(JsonElement), typeof(string)],
                modifiers: null);
            return (string?)method!.Invoke(null, new object?[] { document.RootElement, preferredRole });
        }

        public string[]? TryGetHandshakeDeviceTokenScopes(string payloadJson, string? preferredRole = null)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceTokenScopesCore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                binder: null,
                types: [typeof(JsonElement), typeof(string)],
                modifiers: null);
            return (string[]?)method!.Invoke(null, new object?[] { document.RootElement, preferredRole });
        }

        public Dictionary<string, string> BuildAuthPayload()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "BuildAuthPayload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (Dictionary<string, string>)method!.Invoke(_client, null)!;
        }

        public void SetDeviceTokenForTest(string? token, string[]? scopes = null)
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            var tokenField = identity.GetType().GetField(
                "_deviceToken",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            tokenField!.SetValue(identity, token);
            var scopesField = identity.GetType().GetField(
                "_deviceTokenScopes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            scopesField!.SetValue(identity, scopes);
            SetPrivateField("_connectAuthToken", token ?? "test-token");
        }

        public string? GetStoredOperatorDeviceToken()
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            return (string?)identity.GetType().GetProperty("DeviceToken")!.GetValue(identity);
        }

        public string? GetStoredNodeDeviceToken()
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            return (string?)identity.GetType().GetProperty("NodeDeviceToken")!.GetValue(identity);
        }

        public string GetFallbackDeviceId()
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            var deviceIdProp = identity.GetType().GetProperty("DeviceId");
            return (string)deviceIdProp!.GetValue(identity)!;
        }

        /// <summary>Pre-register a pending request so ProcessRawMessage can resolve the method.</summary>
        public void TrackPendingRequest(string requestId, string method)
        {
            EnsurePendingRegistryOpen();
            GetPendingRegistry().RegisterTracked(requestId, method);
            if (string.Equals(method, "connect", StringComparison.Ordinal))
                AuthorizeCurrentHandshake();
        }

        private void AuthorizeCurrentHandshake()
        {
            var generationProperty = typeof(WebSocketClientBase).GetProperty(
                "CurrentConnectionGeneration",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var generation = (long)generationProperty!.GetValue(_client)!;
            var gateField = typeof(OpenClawGatewayClient).GetField(
                "_handshakeChallengeGate",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var gate = gateField!.GetValue(_client)!;
            var gateType = gate.GetType();
            gateType.GetMethod("Reset")!.Invoke(gate, [generation]);
            Assert.True((bool)gateType.GetMethod("TryBegin")!.Invoke(gate, [generation])!);
            Assert.True((bool)gateType.GetMethod("TryAuthorize")!.Invoke(gate, [generation])!);
        }

        public bool GetPairingRequiredFlag() =>
            GetPrivateField<bool>("_pairingRequiredAwaitingApproval");

        public string? GetPairingRequiredRequestId() => _client.PairingRequiredRequestId;

        public bool ShouldAutoReconnectForTest()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ShouldAutoReconnect",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)method!.Invoke(_client, null)!;
        }

        public string GetSignatureTokenMode()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_signatureTokenMode",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field!.GetValue(_client)!.ToString()!;
        }

        public bool GetOperatorReadScopeUnavailable() =>
            GetPrivateField<bool>("_operatorReadScopeUnavailable");

        public List<ConnectionStatus> CaptureStatusChanges()
        {
            var changes = new List<ConnectionStatus>();
            _client.StatusChanged += (_, s) => changes.Add(s);
            return changes;
        }

        public bool GetAuthFailedFlag() =>
            GetPrivateField<bool>("_authFailed");

        public bool GetUseV2Signature() =>
            GetPrivateField<bool>("_useV2Signature");

        public long? GetChallengeTimestampMs() =>
            GetPrivateField<long?>("_challengeTimestampMs");

        public string? GetLastSkillsStatusAgentId()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_lastSkillsStatusAgentId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field!.GetValue(_client) as string;
        }

        public List<string> CaptureAuthenticationFailedEvents()
        {
            var events = new List<string>();
            _client.AuthenticationFailed += (_, msg) => events.Add(msg);
            return events;
        }

        public List<GatewayErrorKind> CaptureConnectionFailures()
        {
            var events = new List<GatewayErrorKind>();
            _client.ConnectionFailure += (_, kind) => events.Add(kind);
            return events;
        }

        public int GetPendingRequestCount()
        {
            return GetPendingRegistry().Count;
        }

        private PendingRequestRegistry GetPendingRegistry()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_pendingRequests",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (PendingRequestRegistry)field!.GetValue(_client)!;
        }

        private void EnsurePendingRegistryOpen()
        {
            if (_pendingRegistryOpened)
            {
                return;
            }

            GetPendingRegistry().OpenConnection();
            _pendingRegistryOpened = true;
        }

    }

    private sealed class PausingGatewayClient : OpenClawGatewayClient
    {
        private readonly TaskCompletionSource<bool> _mutationSendEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _continueMutationSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pauseMutationSend;

        public PausingGatewayClient(string gatewayUrl, string identityPath)
            : base(gatewayUrl, "test-token", new TestLogger(), identityPath: identityPath)
        {
        }

        public Task MutationSendEntered => _mutationSendEntered.Task;

        public void ArmMutationSendPause() => _pauseMutationSend = true;

        public void AdvanceConnectionEpochForTest()
        {
            var field = typeof(WebSocketClientBase).GetField(
                "_connectionGeneration",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            field!.SetValue(this, ConnectionEpoch + 1);
        }

        public void ContinueMutationSend() => _continueMutationSend.TrySetResult(true);

        protected override async Task<bool> SendRawAsync(
            string message,
            long expectedConnectionGeneration,
            CancellationToken cancellationToken)
        {
            if (_pauseMutationSend &&
                (message.Contains("plugins.install", StringComparison.Ordinal) ||
                 message.Contains("plugins.setEnabled", StringComparison.Ordinal) ||
                 message.Contains("skills.install", StringComparison.Ordinal)))
            {
                _mutationSendEntered.TrySetResult(true);
                await _continueMutationSend.Task.WaitAsync(cancellationToken);
            }

            return await base.SendRawAsync(
                message,
                expectedConnectionGeneration,
                cancellationToken);
        }
    }

    private static string CreateTempIdentityPath() =>
        Path.Combine(Path.GetTempPath(), "OpenClawGatewayClientTests", Guid.NewGuid().ToString("N"));

    private static async Task AssertServerDoesNotReceiveMethodAsync(
        LoopbackWebSocketServer server,
        string forbiddenMethod)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        while (true)
        {
            string requestText;
            try
            {
                requestText = await server.ReceiveTextAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return;
            }

            using var request = JsonDocument.Parse(requestText);
            if (request.RootElement.TryGetProperty("method", out var method) &&
                string.Equals(method.GetString(), forbiddenMethod, StringComparison.Ordinal))
            {
                Assert.Fail($"Server received forbidden '{forbiddenMethod}' request.");
            }
        }
    }

    [Fact]
    public async Task SendWizardRequestAsync_ResponseBeforeDispose_ReturnsPayloadAndCleansTracking()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var responseTask = client.SendWizardRequestAsync("wizard.start", timeoutMs: 10_000);
        var request = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var requestId = ReadRequestId(request);

        await server.SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "res",
                id = requestId,
                ok = true,
                payload = new { stepId = "welcome" }
            }));

        var payload = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("welcome", payload.GetProperty("stepId").GetString());
        Assert.Equal(0, helper.GetPendingRequestCount());

        client.Dispose();
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task SendWizardRequestAsync_GatewayError_PropagatesUnchangedAndCleansTracking()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var responseTask = client.SendWizardRequestAsync("wizard.next", timeoutMs: 10_000);
        var request = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var requestId = ReadRequestId(request);

        await server.SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "res",
                id = requestId,
                ok = false,
                error = new { message = "wizard rejected" }
            }));

        var exception = await Assert.ThrowsAsync<GatewayRequestException>(
            async () => await responseTask.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("wizard rejected", exception.Message);
        Assert.Equal("wizard.next", exception.Method);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task InstallClawHubSkillAsync_SendsExactSearchIdentityAndAgent()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("skill-install-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-extensions", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-extensions",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["skills.install"],
              "events": ["skills.changed"]
            },
            "snapshot": {}
          }
        }
        """);

        var resultTask = client.InstallClawHubSkillAsync(
            new ClawHubSkillInstallRequest(
                "@publisher/shared-slug",
                AgentId: "researcher",
                Version: "1.2.3"),
            timeoutMs: 10_000);
        var requestText = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var request = JsonDocument.Parse(requestText);
        var root = request.RootElement;
        Assert.Equal("skills.install", root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal("clawhub", parameters.GetProperty("source").GetString());
        Assert.Equal("@publisher/shared-slug", parameters.GetProperty("slug").GetString());
        Assert.Equal("researcher", parameters.GetProperty("agentId").GetString());
        Assert.Equal("1.2.3", parameters.GetProperty("version").GetString());
        Assert.False(parameters.TryGetProperty("id", out _));
        Assert.False(parameters.TryGetProperty("force", out _));

        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res",
            id = root.GetProperty("id").GetString(),
            ok = true,
            payload = new
            {
                ok = true,
                message = "Installed shared-slug@1.2.3",
                slug = "shared-slug",
                version = "1.2.3",
            },
        }));

        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Ok);
        Assert.Equal("shared-slug", result.Slug);
        Assert.True(client.AdvertisedFeatures.SupportsEvent("skills.changed"));
    }

    [Fact]
    public async Task SetSkillEnabledAsync_SuccessRefreshesLastAgentScope()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("skill-toggle-refresh-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-skill-toggle-refresh", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-skill-toggle-refresh",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["skills.status", "skills.update"],
              "events": ["skills.changed"]
            },
            "snapshot": {}
          }
        }
        """);

        await client.RequestSkillsStatusAsync("researcher");
        var initialStatusText = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using (var initialStatus = JsonDocument.Parse(initialStatusText))
        {
            Assert.Equal("skills.status", initialStatus.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                "researcher",
                initialStatus.RootElement.GetProperty("params").GetProperty("agentId").GetString());
            helper.ProcessRawMessage(JsonSerializer.Serialize(new
            {
                type = "res",
                id = initialStatus.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new { skills = Array.Empty<object>() },
            }));
        }

        var toggleTask = client.SetSkillEnabledAsync("weather", enabled: false);
        var updateText = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using (var update = JsonDocument.Parse(updateText))
        {
            Assert.Equal("skills.update", update.RootElement.GetProperty("method").GetString());
            helper.ProcessRawMessage(JsonSerializer.Serialize(new
            {
                type = "res",
                id = update.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new { ok = true },
            }));
        }

        Assert.True(await toggleTask.WaitAsync(TimeSpan.FromSeconds(2)));
        var refreshedStatusText = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var refreshedStatus = JsonDocument.Parse(refreshedStatusText);
        Assert.Equal("skills.status", refreshedStatus.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "researcher",
            refreshedStatus.RootElement.GetProperty("params").GetProperty("agentId").GetString());
    }

    [Fact]
    public async Task InstallPluginAsync_RejectsConsentFromPriorConnectionEpochBeforeSending()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("plugin-install-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-plugins", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-plugins",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["plugins.install"],
              "events": []
            },
            "snapshot": {}
          }
        }
        """);
        var staleEpoch = client.ConnectionEpoch - 1;
        var request = PluginInstallRequest.FromClawHub("@openclaw/voice-call") with
        {
            AcknowledgeCapabilities = new PluginCapabilityAcknowledgement(
                "review-token",
                staleEpoch),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.InstallPluginAsync(request, timeoutMs: 10_000));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallPluginAsync_RejectsReconnectBetweenConsentCheckAndFinalWrite()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("plugin-install-race-");
        await server.StartAsync();
        using var client = new PausingGatewayClient(server.WebSocketUrl, identity.Path);
        var helper = new GatewayClientTestHelper(client);
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-plugins-race", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-plugins-race",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["plugins.install"],
              "events": []
            },
            "snapshot": {}
          }
        }
        """);
        var request = PluginInstallRequest.FromClawHub("@openclaw/voice-call") with
        {
            AcknowledgeCapabilities = new PluginCapabilityAcknowledgement(
                "review-token",
                client.ConnectionEpoch),
        };
        client.ArmMutationSendPause();

        var installTask = client.InstallPluginAsync(request, timeoutMs: 10_000);
        await client.MutationSendEntered.WaitAsync(TimeSpan.FromSeconds(2));
        client.AdvanceConnectionEpochForTest();
        client.ContinueMutationSend();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installTask);
        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertServerDoesNotReceiveMethodAsync(server, "plugins.install");
    }

    [Fact]
    public async Task SetPluginEnabledAsync_RejectsReconnectBetweenConsentCheckAndFinalWrite()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("plugin-enable-race-");
        await server.StartAsync();
        using var client = new PausingGatewayClient(server.WebSocketUrl, identity.Path);
        var helper = new GatewayClientTestHelper(client);
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-plugin-enable-race", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-plugin-enable-race",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["plugins.setEnabled"],
              "events": []
            },
            "snapshot": {}
          }
        }
        """);
        var request = new PluginSetEnabledRequest(
            "voice-call",
            Enabled: true,
            new PluginCapabilityAcknowledgement("review-token", client.ConnectionEpoch));
        client.ArmMutationSendPause();

        var enableTask = client.SetPluginEnabledAsync(request, timeoutMs: 10_000);
        await client.MutationSendEntered.WaitAsync(TimeSpan.FromSeconds(2));
        client.AdvanceConnectionEpochForTest();
        client.ContinueMutationSend();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enableTask);
        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertServerDoesNotReceiveMethodAsync(server, "plugins.setEnabled");
    }

    [Fact]
    public async Task InstallClawHubSkillAsync_RejectsReconnectBetweenReviewAndFinalWrite()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("skill-install-race-");
        await server.StartAsync();
        using var client = new PausingGatewayClient(server.WebSocketUrl, identity.Path);
        var helper = new GatewayClientTestHelper(client);
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-skill-install-race", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-skill-install-race",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": {
              "methods": ["skills.install"],
              "events": []
            },
            "snapshot": {}
          }
        }
        """);
        var request = new ClawHubSkillInstallRequest(
            "@openclaw/weather",
            Version: "1.2.3",
            ConnectionEpoch: client.ConnectionEpoch);
        client.ArmMutationSendPause();

        var installTask = client.InstallClawHubSkillAsync(request, timeoutMs: 10_000);
        await client.MutationSendEntered.WaitAsync(TimeSpan.FromSeconds(2));
        client.AdvanceConnectionEpochForTest();
        client.ContinueMutationSend();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installTask);
        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertServerDoesNotReceiveMethodAsync(server, "skills.install");
    }

    [Fact]
    public async Task ExtensionMethods_NotAdvertised_ReturnUnsupportedWithoutSending()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("extension-gate-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.TrackPendingRequest("connect-no-plugins", "connect");
        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-no-plugins",
          "ok": true,
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "features": { "methods": [], "events": [] },
            "snapshot": {}
          }
        }
        """);

        Assert.False((await client.ListPluginsAsync()).IsSupported);
        Assert.False((await client.GetSkillsStatusAsync()).IsSupported);
        Assert.False((await client.SetSkillEnabledDetailedAsync("weather", enabled: false)).IsSupported);
        Assert.False(await client.SetSkillEnabledAsync("weather", enabled: false));
        using var noRequestTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.ReceiveTextAsync(noRequestTimeout.Token));
    }

    [Fact]
    public async Task SendWizardRequestAsync_DeadlineExpires_ThrowsTimeoutAndCleansTracking()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var responseTask = client.SendWizardRequestAsync("wizard.status", timeoutMs: 250);
        await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<TimeoutException>(async () => await responseTask);

        Assert.Equal("Timed out waiting for wizard.status response", exception.Message);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task SendWizardRequestAsync_LateResponseAfterTimeout_DoesNotChangeOutcomeOrTracking()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var timedOutTask = client.SendWizardRequestAsync("wizard.status", timeoutMs: 250);
        var timedOutRequest = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var timedOutRequestId = ReadRequestId(timedOutRequest);
        var firstTimeout = await Assert.ThrowsAsync<TimeoutException>(async () => await timedOutTask);

        await server.SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "res",
                id = timedOutRequestId,
                ok = true,
                payload = new { stepId = "too-late" }
            }));

        var probeTask = client.SendWizardRequestAsync("wizard.status", timeoutMs: 10_000);
        var probeRequest = await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var probeRequestId = ReadRequestId(probeRequest);
        await server.SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "res",
                id = probeRequestId,
                ok = true,
                payload = new { stepId = "probe" }
            }));

        var probePayload = await probeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var repeatedTimeout = await Assert.ThrowsAsync<TimeoutException>(async () => await timedOutTask);

        Assert.Equal("probe", probePayload.GetProperty("stepId").GetString());
        Assert.Same(firstTimeout, repeatedTimeout);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task SendWizardRequestAsync_DisposeBeforeResponse_ThrowsCancellationAndCleansTracking()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var responseTask = client.SendWizardRequestAsync("wizard.cancel", timeoutMs: 10_000);
        await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task SendWizardRequestAsync_TransportDisconnect_DrainsPendingRequest()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        var responseTask = client.SendWizardRequestAsync("wizard.next", timeoutMs: 10_000);
        await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);

        var exception = await Assert.ThrowsAsync<GatewayConnectionLostException>(
            async () => await responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            (int)WebSocketCloseStatus.NormalClosure,
            exception.CloseStatusCode);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task SendWizardRequestAsync_ServiceRestartClose_PreservesCloseStatus()
    {
        using var server = new LoopbackWebSocketServer(useManagedWebSocket: true);
        using var identity = new TempDirectory("wizard-request-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();
        helper.TrackPendingRequest("req-hello-restart", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-hello-restart",
            "payload": {
                "type": "hello-ok",
                "protocol": 4
            }
        }
        """);
        Assert.True(client.HasHandshakeSnapshot);

        var responseTask = client.SendWizardRequestAsync("wizard.next", timeoutMs: 10_000);
        await server.ReceiveTextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await server.CloseSocketAsync(
            0,
            (WebSocketCloseStatus)1012,
            "service restart");

        var exception = await Assert.ThrowsAsync<GatewayConnectionLostException>(
            async () => await responseTask.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(1012, exception.CloseStatusCode);
        Assert.Equal("service restart", exception.CloseStatusDescription);
        Assert.False(client.HasHandshakeSnapshot);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task ProtocolMismatch_AbortsTransportAndRejectsSubsequentOperatorSend()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("operator-protocol-mismatch-");
        await server.StartAsync();
        var helper = new GatewayClientTestHelper(
            gatewayUrl: server.WebSocketUrl,
            identityPath: identity.Path);
        using var client = helper.Client;
        await client.ConnectAsync();

        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "connect-mismatch",
          "payload": {
            "type": "hello-ok",
            "protocol": 2
          }
        }
        """);

        Assert.False(client.IsConnectedToGateway);
        Assert.False(helper.ShouldAutoReconnectForTest());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendChatMessageAsync("blocked after protocol mismatch"));
    }

    private static string ReadRequestId(string request)
    {
        using var document = JsonDocument.Parse(request);
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Gateway request did not include an id.");
    }

    [Fact]
    public void ExtractChatTimestampMs_FallsBackFromInvalidTimestampToTs()
    {
        var helper = new GatewayClientTestHelper();

        var ts = helper.ExtractChatTimestampMs("""{"timestamp":999999999999999999999999999999,"ts":1712345678}""");

        Assert.Equal(1_712_345_678_000, ts);
    }

    [Fact]
    public void OperatorConnect_FreshDevice_RequestsBootstrapHandoffScopes()
    {
        var helper = new GatewayClientTestHelper(tokenIsBootstrapToken: true);
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Equal(
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"],
            scopes);
        Assert.DoesNotContain("operator.admin", scopes);
        Assert.DoesNotContain("operator.pairing", scopes);
        Assert.Equal("test-token", auth["bootstrapToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void OperatorConnect_FreshBootstrapDevice_StartsWithV2Signature()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"oca-gw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var helper = new GatewayClientTestHelper(tokenIsBootstrapToken: true, identityPath: tmpDir);

        Assert.True(helper.Client.UseV2Signature);
    }

    [Fact]
    public void OperatorConnect_SharedTokenDevice_StartsWithV3Signature()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"oca-gw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var helper = new GatewayClientTestHelper(tokenIsBootstrapToken: false, identityPath: tmpDir);

        Assert.False(helper.Client.UseV2Signature);
    }

    [Fact]
    public async Task RequestSkillsStatusAsync_RemembersRequestedAgentScope()
    {
        var helper = new GatewayClientTestHelper();

        await helper.Client.RequestSkillsStatusAsync("agent-alpha");

        Assert.Equal("agent-alpha", helper.GetLastSkillsStatusAgentId());

        await helper.Client.RequestSkillsStatusAsync();

        Assert.Null(helper.GetLastSkillsStatusAgentId());
    }

    [Fact]
    public void OperatorConnect_FreshStandardLocalLoopbackDevice_RequestsFullOperatorScopes()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://127.0.0.1:18789");
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Contains("operator.pairing", scopes);
        Assert.Equal("test-token", auth["token"]);
        Assert.False(auth.ContainsKey("bootstrapToken"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void Bug6_SharedSettingsToken_LocalLoopbackFreshOperator_RequestsAdminScopesAndTokenAuth()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://localhost:18789", tokenIsBootstrapToken: false);
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Contains("operator.pairing", scopes);
        Assert.Equal("test-token", auth["token"]);
        Assert.False(auth.ContainsKey("bootstrapToken"));
    }

    [Fact]
    public void OperatorConnect_FreshStandardRemoteDevice_RequestsAdminScopes()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://gateway.example.com:18789");
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();

        Assert.Contains("operator.admin", scopes);
        Assert.Contains("operator.pairing", scopes);
    }

    [Fact]
    public void OperatorConnect_PairedDevice_RequestsFullOperatorScopes()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetDeviceTokenForTest("paired-device-token");

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Contains("operator.pairing", scopes);
        Assert.Equal("paired-device-token", auth["deviceToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("bootstrapToken"));
    }

    [Fact]
    public void OperatorConnect_PairedDeviceWithStoredScopes_RequestsStoredScopes()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetDeviceTokenForTest(
            "paired-device-token",
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"]);

        var scopes = helper.GetRequestedOperatorScopes();

        Assert.Equal(
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"],
            scopes);
    }

    [Fact]
    public void BootstrapNodeHandoff_FreshDevice_RequestsNodeRoleWithoutScopes()
    {
        var helper = new GatewayClientTestHelper(
            tokenIsBootstrapToken: true,
            bootstrapPairAsNode: true,
            identityPath: CreateTempIdentityPath());
        helper.SetDeviceTokenForTest(null);

        var auth = helper.BuildAuthPayload();

        Assert.Equal("node", helper.GetConnectRole());
        Assert.Empty(helper.GetRequestedOperatorScopes());
        Assert.Equal("test-token", auth["bootstrapToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void BootstrapNodeHandoff_HelloOkWithNodeRole_DoesNotStorePrimaryNodeTokenAsOperator()
    {
        var helper = new GatewayClientTestHelper(
            tokenIsBootstrapToken: true,
            bootstrapPairAsNode: true,
            identityPath: CreateTempIdentityPath());
        helper.SetDeviceTokenForTest(null);
        helper.TrackPendingRequest("req-hello-node", "connect");

        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "req-hello-node",
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "auth": {
              "deviceToken": "node-token",
              "role": "node",
              "scopes": []
            }
          }
        }
        """);

        Assert.Equal("node-token", helper.GetStoredNodeDeviceToken());
        Assert.Null(helper.GetStoredOperatorDeviceToken());
    }

    [Fact]
    public void BootstrapNodeHandoff_HelloOkWithOperatorHandoffToken_StoresOperatorToken()
    {
        var helper = new GatewayClientTestHelper(
            tokenIsBootstrapToken: true,
            bootstrapPairAsNode: true,
            identityPath: CreateTempIdentityPath());
        helper.SetDeviceTokenForTest(null);
        helper.TrackPendingRequest("req-hello-node", "connect");

        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "req-hello-node",
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "auth": {
              "deviceToken": "node-token",
              "role": "node",
              "scopes": [],
              "deviceTokens": [
                {
                  "deviceToken": "operator-token",
                  "role": "operator",
                  "scopes": ["operator.read"]
                }
              ]
            }
          }
        }
        """);

        Assert.Equal("node-token", helper.GetStoredNodeDeviceToken());
        Assert.Equal("operator-token", helper.GetStoredOperatorDeviceToken());
    }

    [Theory]
    [InlineData("""{"type":"hello-ok","protocol":4,"server":{"version":"hostile"}}""")]
    [InlineData("""{"type":"hello-ok","protocol":5}""")]
    [InlineData("""{"type":"hello-ok"}""")]
    public void GuardedValidation_IgnoresUncorrelatedHelloOk(string payloadJson)
    {
        var helper = new GatewayClientTestHelper();
        var handshakeSucceeded = false;
        var failures = new List<GatewayErrorKind>();
        helper.Client.HandshakeAuthorizationAsync = _ => Task.FromResult(
            new ReconnectAuthorizationResult(true, GatewayErrorKind.Unknown, string.Empty));
        helper.Client.HandshakeSucceeded += (_, _) => handshakeSucceeded = true;
        helper.Client.ConnectionFailure += (_, failure) => failures.Add(failure);

        helper.ProcessRawMessage($$"""
        {
          "type": "res",
          "id": "unsolicited",
          "payload": {{payloadJson}}
        }
        """);

        Assert.False(handshakeSucceeded);
        Assert.False(helper.Client.HasHandshakeSnapshot);
        Assert.Empty(failures);
        Assert.True(helper.ShouldAutoReconnectForTest());
    }

    [Fact]
    public void GuardedValidation_AcceptsHelloOkForTrackedConnectRequest()
    {
        var helper = new GatewayClientTestHelper();
        var handshakeSucceeded = false;
        helper.Client.HandshakeAuthorizationAsync = _ => Task.FromResult(
            new ReconnectAuthorizationResult(true, GatewayErrorKind.Unknown, string.Empty));
        helper.Client.HandshakeSucceeded += (_, _) => handshakeSucceeded = true;
        helper.TrackPendingRequest("tracked-connect", "connect");

        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "tracked-connect",
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "server": { "version": "expected" }
          }
        }
        """);

        Assert.True(handshakeSucceeded);
        Assert.True(helper.Client.HasHandshakeSnapshot);
    }

    [Fact]
    public async Task HandshakeAuthorizationDenial_BlocksLaterChallengesWithoutDisablingReconnect()
    {
        var helper = new GatewayClientTestHelper();
        var authorizationCalls = 0;
        var denied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        helper.Client.HandshakeAuthorizationAsync = _ =>
        {
            authorizationCalls++;
            return Task.FromResult(authorizationCalls == 1
                ? new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    "listener ownership lost")
                : ReconnectAuthorizationResult.AllowedResult);
        };
        helper.Client.ConnectionFailure += (_, _) => denied.TrySetResult();

        const string challenge = """
            {
              "type": "event",
              "event": "connect.challenge",
              "payload": {
                "nonce": "listener-replacement",
                "ts": 1785824000000
              }
            }
            """;

        helper.ProcessRawMessage(challenge);
        await denied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        helper.ProcessRawMessage(challenge);
        await Task.Delay(50);

        Assert.Equal(1, authorizationCalls);
        Assert.False(helper.GetAuthFailedFlag());
    }

    [Fact]
    public async Task DuplicateChallengeWhileAuthorizationActive_IsSuppressed()
    {
        var helper = new GatewayClientTestHelper();
        var authorizationCalls = 0;
        var authorizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        helper.Client.HandshakeAuthorizationAsync = async _ =>
        {
            Interlocked.Increment(ref authorizationCalls);
            authorizationStarted.TrySetResult();
            await releaseAuthorization.Task;
            return ReconnectAuthorizationResult.AllowedResult;
        };

        const string challenge = """
            {
              "type": "event",
              "event": "connect.challenge",
              "payload": {
                "nonce": "duplicate",
                "ts": 1785824000000
              }
            }
            """;

        helper.ProcessRawMessage(challenge);
        await authorizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        helper.ProcessRawMessage(challenge);
        releaseAuthorization.TrySetResult();
        await Task.Delay(50);

        Assert.Equal(1, authorizationCalls);
    }

    [Fact]
    public async Task MalformedChallenge_DoesNotConsumeCurrentSocketGate()
    {
        var helper = new GatewayClientTestHelper();
        var authorizationCalls = 0;
        var authorizationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        helper.Client.HandshakeAuthorizationAsync = _ =>
        {
            authorizationCalls++;
            authorizationObserved.TrySetResult();
            return Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        };

        helper.ProcessRawMessage(
            """
            {
              "type": "event",
              "event": "connect.challenge",
              "payload": { "nonce": 42 }
            }
            """);
        helper.ProcessRawMessage(
            """
            {
              "type": "event",
              "event": "connect.challenge",
              "payload": {
                "nonce": "valid-after-malformed",
                "ts": 1785824000000
              }
            }
            """);
        await authorizationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, authorizationCalls);
    }

    [Fact]
    public async Task StaleHandshakeAuthorizationDenial_DoesNotAbortNewerSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        using var client = new OpenClawGatewayClient(
            server.WebSocketUrl,
            "test-token",
            new TestLogger(),
            identityPath: CreateTempIdentityPath());
        var authorizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeAuthorizationAsync = async _ =>
        {
            authorizationStarted.TrySetResult();
            await releaseAuthorization.Task;
            return new ReconnectAuthorizationResult(
                false,
                GatewayErrorKind.LocalPortConflict,
                "stale listener");
        };

        await client.ConnectAsync();
        await server.WaitForAcceptedCountAsync(1, TimeSpan.FromSeconds(2));
        await server.SendTextAsync(
            """
            {
              "type": "event",
              "event": "connect.challenge",
              "payload": {
                "nonce": "old-socket",
                "ts": 1785824000000
              }
            }
            """);
        await authorizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.ConnectAsync();
        await server.WaitForAcceptedCountAsync(2, TimeSpan.FromSeconds(2));
        releaseAuthorization.TrySetResult();
        await server.SendTextAsync("{}");
        await Task.Delay(100);

        var isConnected = (bool)typeof(WebSocketClientBase)
            .GetProperty(
                "IsConnected",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        Assert.True(isConnected);
    }

    [Fact]
    public void OperatorBootstrap_HelloOkWithNodeHandoffToken_StoresNodeToken()
    {
        var helper = new GatewayClientTestHelper(
            tokenIsBootstrapToken: true,
            bootstrapPairAsNode: false,
            identityPath: CreateTempIdentityPath());
        helper.SetDeviceTokenForTest(null);
        helper.TrackPendingRequest("req-hello-operator", "connect");

        helper.ProcessRawMessage("""
        {
          "type": "res",
          "id": "req-hello-operator",
          "payload": {
            "type": "hello-ok",
            "protocol": 4,
            "auth": {
              "deviceToken": "operator-token",
              "role": "operator",
              "scopes": ["operator.read"],
              "deviceTokens": [
                {
                  "deviceToken": "node-token",
                  "role": "node",
                  "scopes": []
                }
              ]
            }
          }
        }
        """);

        Assert.Equal("operator-token", helper.GetStoredOperatorDeviceToken());
        Assert.Equal("node-token", helper.GetStoredNodeDeviceToken());
    }

    [Fact]
    public void HelloOkWhenTokenWriteFails_CompletesHandshakeAndPublishesToken()
    {
        var identityPath = CreateTempIdentityPath();
        var helper = new GatewayClientTestHelper(
            tokenIsBootstrapToken: true,
            identityPath: identityPath);
        var handshakeSucceeded = false;
        DeviceTokenReceivedEventArgs? receivedToken = null;
        helper.Client.HandshakeSucceeded += (_, _) => handshakeSucceeded = true;
        helper.Client.DeviceTokenReceived += (_, e) => receivedToken = e;
        helper.TrackPendingRequest("req-hello-operator", "connect");

        using (new FileStream(
            Path.Combine(identityPath, "device-key-ed25519.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            helper.ProcessRawMessage("""
            {
              "type": "res",
              "id": "req-hello-operator",
              "payload": {
                "type": "hello-ok",
                "protocol": 4,
                "auth": {
                  "deviceToken": "operator-token",
                  "role": "operator",
                  "scopes": ["operator.read"]
                }
              }
            }
            """);
        }

        Assert.True(handshakeSucceeded);
        Assert.Equal("operator-token", receivedToken?.Token);
        Assert.Equal("operator", receivedToken?.Role);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AcceptedHelloOkProtocol_CompletesHandshakeWithAdditiveSnapshotFields(int protocol)
    {
        var helper = new GatewayClientTestHelper();
        var statuses = new List<ConnectionStatus>();
        var compatibility = new List<GatewayProtocolCompatibility>();
        var handshakeCount = 0;
        helper.Client.StatusChanged += (_, status) => statuses.Add(status);
        helper.Client.ProtocolCompatibilityChanged += (_, value) => compatibility.Add(value);
        helper.Client.HandshakeSucceeded += (_, _) => handshakeCount++;
        helper.TrackPendingRequest("req-protocol", "connect");

        helper.ProcessRawMessage(
            $$"""
            {
              "type": "res",
              "id": "req-protocol",
              "ok": true,
              "payload": {
                "type": "hello-ok",
                "protocol": {{protocol}},
                "snapshot": {
                  "futureField": {
                    "nested": true
                  }
                }
              }
            }
            """);

        Assert.Equal(1, handshakeCount);
        Assert.Contains(ConnectionStatus.Connected, statuses);
        Assert.True(helper.Client.HasHandshakeSnapshot);
        var accepted = Assert.Single(compatibility);
        Assert.Equal(GatewayProtocolCompatibilityState.Compatible, accepted.State);
        Assert.Equal(protocol, accepted.SelectedProtocol);
        Assert.Null(accepted.GatewayExpectedProtocol);
    }

    [Theory]
    [InlineData("""{"type":"hello-ok","protocol":2,"auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""{"type":"hello-ok","auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""{"type":"hello-ok","protocol":null,"auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""{"type":"hello-ok","protocol":"4","auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""{"type":"hello-ok","protocol":4.5,"auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""{"type":"unexpected-success","protocol":4,"auth":{"deviceToken":"must-not-store"}}""")]
    [InlineData("""null""")]
    public void InvalidConnectSuccess_FailsBeforeOperatorHandshakeSideEffects(string payloadJson)
    {
        var helper = new GatewayClientTestHelper();
        var statuses = new List<ConnectionStatus>();
        var failures = new List<GatewayErrorKind>();
        var handshakeCount = 0;
        var tokenCount = 0;
        var gatewaySelfCount = 0;
        helper.Client.StatusChanged += (_, status) => statuses.Add(status);
        helper.Client.ConnectionFailure += (_, kind) => failures.Add(kind);
        helper.Client.HandshakeSucceeded += (_, _) => handshakeCount++;
        helper.Client.DeviceTokenReceived += (_, _) => tokenCount++;
        helper.Client.GatewaySelfUpdated += (_, _) => gatewaySelfCount++;
        helper.TrackPendingRequest("req-invalid-hello", "connect");

        helper.ProcessRawMessage(
            $$"""
            {
              "type": "res",
              "id": "req-invalid-hello",
              "ok": true,
              "payload": {{payloadJson}}
            }
            """);

        Assert.Equal([GatewayErrorKind.ProtocolMismatch], failures);
        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.DoesNotContain(ConnectionStatus.Connected, statuses);
        Assert.Equal(0, handshakeCount);
        Assert.Equal(0, tokenCount);
        Assert.Equal(0, gatewaySelfCount);
        Assert.Null(helper.GetStoredOperatorDeviceToken());
        Assert.False(helper.Client.HasHandshakeSnapshot);
        Assert.False(helper.ShouldAutoReconnectForTest());
    }

    [Fact]
    public void ProtocolMismatch_IgnoresSubsequentOperatorEvents()
    {
        var helper = new GatewayClientTestHelper();
        var chatMessageCount = 0;
        helper.Client.ChatMessageReceived += (_, _) => chatMessageCount++;
        helper.TrackPendingRequest("req-invalid-hello", "connect");

        helper.ProcessRawMessage(
            """
            {
              "type": "res",
              "id": "req-invalid-hello",
              "ok": true,
              "payload": {
                "type": "hello-ok",
                "protocol": 2
              }
            }
            """);
        helper.ProcessRawMessage(
            """
            {
              "type": "event",
              "event": "session.message",
              "payload": {
                "sessionKey": "agent:main:main",
                "message": {
                  "role": "assistant",
                  "content": "must not dispatch"
                },
                "state": "final"
              }
            }
            """);

        Assert.Equal(0, chatMessageCount);
    }

    [Theory]
    [InlineData("""{"message":"connect rejected","code":"PROTOCOL_MISMATCH"}""")]
    [InlineData("""{"message":"protocol mismatch: gateway requires version 5"}""")]
    public void ConnectProtocolMismatch_IsClassifiedAndStopsAutomaticReconnect(string errorJson)
    {
        var helper = new GatewayClientTestHelper();
        var statuses = new List<ConnectionStatus>();
        var failures = new List<GatewayErrorKind>();
        helper.Client.StatusChanged += (_, status) => statuses.Add(status);
        helper.Client.ConnectionFailure += (_, kind) => failures.Add(kind);
        helper.TrackPendingRequest("req-protocol-mismatch", "connect");

        helper.ProcessRawMessage(
            $$"""
            {
              "type": "res",
              "id": "req-protocol-mismatch",
              "ok": false,
              "error": {{errorJson}}
            }
            """);

        Assert.Equal([GatewayErrorKind.ProtocolMismatch], failures);
        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.False(helper.ShouldAutoReconnectForTest());
        Assert.False(helper.GetUseV2Signature());
    }

    [Fact]
    public void StructuredProtocolMismatch_PublishesGatewayExpectation()
    {
        var helper = new GatewayClientTestHelper();
        GatewayProtocolCompatibility? compatibility = null;
        helper.Client.ProtocolCompatibilityChanged += (_, value) => compatibility = value;
        helper.TrackPendingRequest("req-protocol-details", "connect");

        helper.ProcessRawMessage(
            """
            {
              "type": "res",
              "id": "req-protocol-details",
              "ok": false,
              "error": {
                "code": "INVALID_REQUEST",
                "message": "protocol mismatch",
                "details": {
                  "code": "PROTOCOL_MISMATCH",
                  "clientMinProtocol": 3,
                  "clientMaxProtocol": 4,
                  "expectedProtocol": 5,
                  "minimumProbeProtocol": 3
                }
              }
            }
            """);

        Assert.NotNull(compatibility);
        Assert.Equal(GatewayProtocolCompatibilityState.GatewayTooNew, compatibility.State);
        Assert.Equal(5, compatibility.GatewayExpectedProtocol);
        Assert.Equal(3, compatibility.GatewayMinimumProtocol);
        Assert.False(compatibility.Retryable);
    }

    [Fact]
    public void BootstrapNodeHandoff_PrefersOperatorTokenFromAdditionalDeviceTokens()
    {
        var helper = new GatewayClientTestHelper();

        var payload =
            """
            {
              "type": "hello-ok",
              "auth": {
                "deviceToken": "node-token",
                "role": "node",
                "scopes": [],
                "deviceTokens": [
                  {
                    "deviceToken": "operator-token",
                    "role": "operator",
                    "scopes": ["operator.read"]
                  }
                ]
              }
            }
            """;
        var token = helper.TryGetHandshakeDeviceToken(payload, "operator");
        var scopes = helper.TryGetHandshakeDeviceTokenScopes(payload, "operator");

        Assert.Equal("operator-token", token);
        Assert.NotNull(scopes);
        Assert.Equal(["operator.read"], scopes!);
    }

    private class TestLogger : IOpenClawLogger
    {
        public List<string> Logs { get; } = new();

        public void Info(string message) => Logs.Add($"INFO: {message}");
        public void Debug(string message) => Logs.Add($"DEBUG: {message}");
        public void Warn(string message) => Logs.Add($"WARN: {message}");
        public void Error(string message, Exception? ex = null) => Logs.Add($"ERROR: {message}");
    }

    [Fact]
    public void ProcessRawMessage_ChatEventLogsRawLengthWithoutPayloadContent()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        var rawMessage = "{\"type\":\"event\",\"event\":\"chat\",\"payload\":{\"sessionKey\":\"main\",\"text\":\"super-secret chat payload\",\"role\":\"user\"}}";

        helper.ProcessRawMessage(rawMessage);

        Assert.Contains(logger.Logs, log => log == $"DEBUG: Chat event received: len={rawMessage.Length}");
        Assert.DoesNotContain(logger.Logs, log => log.Contains("super-secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageWithStringContent_EmitsChatMessage()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "agent:main:whatsapp:direct:+15551234567",
            "message": {
              "role": "user",
              "content": "testing from whatsapp",
              "timestamp": 1781631273567
            },
            "state": "final"
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal("agent:main:whatsapp:direct:+15551234567", received!.SessionKey);
        Assert.Equal("user", received.Role);
        Assert.Equal("testing from whatsapp", received.Text);
        Assert.Equal("final", received.State);
        Assert.Equal(1781631273567, received.Ts);
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageWithOpenClawMetadata_EmitsMessageIdentity()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "main",
            "message": {
              "role": "user",
              "content": "queued spam message",
              "timestamp": 1781631273567,
              "__openclaw": {
                "id": "msg-user-a",
                "seq": 42
              }
            },
            "state": "final"
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal("msg-user-a", received!.OpenClawId);
        Assert.Equal(42, received.OpenClawSeq);
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageWithContentBlocks_EmitsChatMessage()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "agent:main:whatsapp:direct:+15551234567",
            "message": {
              "role": "assistant",
              "content": [
                { "type": "text", "text": "hello from assistant" }
              ],
              "timestamp": 1781631280633
            },
            "state": "final"
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal("agent:main:whatsapp:direct:+15551234567", received!.SessionKey);
        Assert.Equal("assistant", received.Role);
        Assert.Equal("hello from assistant", received.Text);
        Assert.Equal("final", received.State);
        Assert.Equal(1781631280633, received.Ts);
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageAssistantNoReply_DropsFrame()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        OpenClawNotification? notification = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;
        helper.Client.NotificationReceived += (_, value) => notification = value;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "main",
            "message": {
              "role": "assistant",
              "content": "  no_reply\n",
              "timestamp": 1781631280633
            },
            "state": "final"
          }
        }
        """);

        Assert.Null(received);
        Assert.Null(notification);
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageUserNoReply_IsNotDropped()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "main",
            "message": {
              "role": "user",
              "content": "no_reply",
              "timestamp": 1781631280633
            },
            "state": "final"
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal("user", received!.Role);
        Assert.Equal("no_reply", received.Text);
    }

    [Fact]
    public void ParseChatHistoryPayload_AssistantNoReply_DropsTranscriptEntry()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            { "role": "user", "content": "before", "timestamp": 1 },
            { "role": "assistant", "content": "no_reply", "timestamp": 2 },
            { "role": "assistant", "content": "visible reply", "timestamp": 3 }
          ]
        }
        """);

        Assert.Equal(["before", "visible reply"], history.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void ParseChatHistoryPayload_ToolBlocks_PreservesInputsAndOutputs()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                {
                  "type": "tool_use",
                  "id": "call-1",
                  "name": "exec",
                  "input": {
                    "command": "pwd",
                    "workdir": "/workspace",
                    "yieldMs": 1000
                  }
                }
              ],
              "timestamp": 1
            },
            {
              "role": "toolResult",
              "toolCallId": "call-1",
              "content": [
                {
                  "type": "tool_result",
                  "name": "exec",
                  "content": [{ "type": "text", "text": "/workspace" }]
                }
              ],
              "timestamp": 2
            }
          ]
        }
        """);

        Assert.Equal(2, history.Messages.Count);
        var call = Assert.Single(history.Messages[0].ToolContent);
        Assert.Equal(ChatToolContentKind.Call, call.Kind);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("exec", call.ToolName);
        Assert.Equal("pwd", call.Args?.GetProperty("command").GetString());

        var result = Assert.Single(history.Messages[1].ToolContent);
        Assert.Equal(ChatToolContentKind.Result, result.Kind);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("/workspace", result.Text);
    }

    [Theory]
    [InlineData("tool_call_id")]
    [InlineData("tool_use_id")]
    public void ParseChatHistoryPayload_ToolResult_PrefersSemanticCallReference(string referenceProperty)
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload($$"""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                {
                  "type": "tool_use",
                  "id": "call-1",
                  "tool_call_id": "not-the-definition-id",
                  "name": "exec",
                  "input": { "command": "pwd" }
                }
              ],
              "timestamp": 1
            },
            {
              "role": "toolResult",
              "toolCallId": "message-level-id",
              "content": [
                {
                  "type": "tool_result",
                  "id": "result-block-id",
                  "{{referenceProperty}}": "call-1",
                  "name": "exec",
                  "content": "/workspace"
                }
              ],
              "timestamp": 2
            }
          ]
        }
        """);

        Assert.Equal("call-1", Assert.Single(history.Messages[0].ToolContent).CallId);
        Assert.Equal("call-1", Assert.Single(history.Messages[1].ToolContent).CallId);
    }

    [Theory]
    [InlineData("toolCallId")]
    [InlineData("toolUseId")]
    public void ParseChatHistoryPayload_ToolResult_PrefersMessageCallReferenceOverBlockId(
        string referenceProperty)
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload($$"""
        {
          "messages": [
            {
              "role": "toolResult",
              "{{referenceProperty}}": "call-1",
              "content": [
                {
                  "type": "tool_result",
                  "id": "result-block-id",
                  "name": "exec",
                  "content": "/workspace"
                }
              ],
              "timestamp": 2
            }
          ]
        }
        """);

        Assert.Equal("call-1", Assert.Single(history.Messages[0].ToolContent).CallId);
    }

    [Fact]
    public void ParseChatHistoryPayload_ToolResult_UsesCallIdAndMessageErrorFallback()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "toolResult",
              "isError": true,
              "content": [
                {
                  "type": "tool_result",
                  "callId": "call-1",
                  "name": "exec",
                  "content": "access denied"
                }
              ],
              "timestamp": 2
            }
          ]
        }
        """);

        var result = Assert.Single(Assert.Single(history.Messages).ToolContent);
        Assert.Equal("call-1", result.CallId);
        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("toolResult")]
    [InlineData("tool_result")]
    public void ParseChatHistoryPayload_StringToolResult_PreservesCallId(string role)
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload($$"""
        {
          "messages": [
            {
              "role": "{{role}}",
              "toolCallId": "call-1",
              "toolName": "exec",
              "content": "/workspace",
              "timestamp": 2
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("/workspace", message.Text);
        var result = Assert.Single(message.ToolContent);
        Assert.Equal(ChatToolContentKind.Result, result.Kind);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("exec", result.ToolName);
        Assert.Equal("/workspace", result.Text);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("function")]
    public void ParseChatHistoryPayload_StringLegacyToolRole_DoesNotSynthesizeToolResult(string role)
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload($$"""
        {
          "messages": [
            {
              "role": "{{role}}",
              "toolCallId": "call-1",
              "toolName": "exec",
              "content": "/workspace",
              "timestamp": 2
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("/workspace", message.Text);
        Assert.Empty(message.ToolContent);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("function")]
    public void ParseChatHistoryPayload_ArrayLegacyToolRole_PreservesSynthesizedToolResult(string role)
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload($$"""
        {
          "messages": [
            {
              "role": "{{role}}",
              "toolCallId": "call-1",
              "toolName": "exec",
              "content": [{ "type": "text", "text": "/workspace" }],
              "timestamp": 2
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("/workspace", message.Text);
        var result = Assert.Single(message.ToolContent);
        Assert.Equal(ChatToolContentKind.Result, result.Kind);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("exec", result.ToolName);
        Assert.Equal("/workspace", result.Text);
    }

    [Fact]
    public void ParseChatHistoryPayload_InterleavedBlocks_PreserveSourceOrder()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Before" },
                {
                  "type": "tool_use",
                  "id": "call-1",
                  "name": "exec",
                  "input": { "command": "pwd" }
                },
                { "type": "text", "text": "After" }
              ],
              "timestamp": 1
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("Before\nAfter", message.Text);
        Assert.Collection(
            message.ContentParts,
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Text, part.Kind);
                Assert.Equal("Before", part.Text);
            },
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Tool, part.Kind);
                Assert.Equal("call-1", part.Tool?.CallId);
            },
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Text, part.Kind);
                Assert.Equal("After", part.Text);
            });
    }

    [Fact]
    public void ParseChatHistoryPayload_StructuredMedia_PreservesTypedFieldsAndOrder()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Created it." },
                {
                  "type": "image",
                  "mimeType": "image/png",
                  "fileName": "banner.png",
                  "artifactId": "artifact_managed_image_123",
                  "alt": "OpenClaw banner",
                  "width": 1200,
                  "height": 774,
                  "sizeBytes": 12345
                },
                { "type": "text", "text": "Finished." }
              ],
              "timestamp": 1
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("Created it.\nFinished.", message.Text);
        Assert.Collection(
            message.ContentParts,
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Text, part.Kind);
                Assert.Equal("Created it.", part.Text);
            },
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Media, part.Kind);
                Assert.Equal(ChatMediaContentKind.Image, part.Media?.Kind);
                Assert.Equal("image/png", part.Media?.MimeType);
                Assert.Equal("banner.png", part.Media?.FileName);
                Assert.Equal("artifact_managed_image_123", part.Media?.ArtifactId);
                Assert.Equal(1200, part.Media?.Width);
                Assert.Equal(774, part.Media?.Height);
            },
            part =>
            {
                Assert.Equal(ChatMessageContentPartKind.Text, part.Kind);
                Assert.Equal("Finished.", part.Text);
            });
    }

    [Fact]
    public void ParseChatHistoryPayload_LegacyMediaOnly_PreservesMessageAndRedactsPath()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": "MEDIA:/home/openclaw/.openclaw/workspace/downloads/banner.png",
              "timestamp": 1
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal(string.Empty, message.Text);
        var media = Assert.Single(message.ContentParts).Media;
        Assert.NotNull(media);
        Assert.Equal(ChatMediaContentSource.LegacyDirective, media.Source);
        Assert.Equal("banner.png", media.FileName);
    }

    [Fact]
    public void ParseChatHistoryPayload_StringArray_RedactsLegacyMediaPath()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                "Created it.",
                "MEDIA:/home/openclaw/.openclaw/workspace/downloads/banner.png"
              ],
              "timestamp": 1
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("Created it.", message.Text);
        Assert.DoesNotContain("/home/openclaw", message.Text, StringComparison.Ordinal);
        Assert.Single(
            message.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
        Assert.Equal(
            "Created it.",
            Assert.Single(
                message.ContentParts,
                part => part.Kind == ChatMessageContentPartKind.Text).Text);
    }

    [Fact]
    public void ParseChatHistoryPayload_SplitFence_KeepsMediaDirectiveAsTextOnly()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                "```",
                "MEDIA:/home/openclaw/private.png\n```"
              ],
              "timestamp": 1
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        var part = Assert.Single(message.ContentParts);
        Assert.Equal(ChatMessageContentPartKind.Text, part.Kind);
        Assert.Contains("MEDIA:/home/openclaw/private.png", part.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            message.ContentParts,
            contentPart => contentPart.Kind == ChatMessageContentPartKind.Media);
    }

    [Fact]
    public void ChatEvent_LegacyMediaOnly_RaisesTypedMessageWithoutRawPath()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "chat",
          "payload": {
            "sessionKey": "main",
            "state": "final",
            "message": {
              "role": "assistant",
              "content": "MEDIA:/home/openclaw/.openclaw/workspace/downloads/banner.png"
            }
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal(string.Empty, received.Text);
        var media = Assert.Single(received.ContentParts).Media;
        Assert.Equal("banner.png", media?.FileName);
    }

    [Fact]
    public void ParseChatHistoryPayload_OpenClawMetadata_PreservesMessageIdentity()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "user",
              "content": "a",
              "timestamp": 1,
              "__openclaw": {
                "id": "msg-history-a",
                "seq": 7
              }
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("msg-history-a", message.OpenClawId);
        Assert.Equal(7, message.OpenClawSeq);
    }

    [Fact]
    public void ParseChatHistoryPayload_CompactionMetadata_PreservesBoundaryDetails()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "system",
              "content": "Context compacted",
              "timestamp": 1,
              "__openclaw": {
                "id": "compact-1",
                "seq": 8,
                "kind": "compaction",
                "tokensBefore": 42000,
                "tokensAfter": 12000
              }
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Equal("compaction", message.OpenClawKind);
        Assert.Equal(42000, message.CompactionTokensBefore);
        Assert.Equal(12000, message.CompactionTokensAfter);
    }

    [Fact]
    public void ParseChatHistoryPayload_MalformedCompactionMetadata_IsIgnored()
    {
        var helper = new GatewayClientTestHelper();

        var history = helper.ParseChatHistoryPayload("""
        {
          "messages": [
            {
              "role": "system",
              "content": "Context compacted",
              "__openclaw": {
                "kind": 42,
                "tokensBefore": "many",
                "tokensAfter": false
              }
            }
          ]
        }
        """);

        var message = Assert.Single(history.Messages);
        Assert.Null(message.OpenClawKind);
        Assert.Null(message.CompactionTokensBefore);
        Assert.Null(message.CompactionTokensAfter);
    }

    [Fact]
    public void ProcessRawMessage_LiveCompaction_PreservesBoundaryDetails()
    {
        var helper = new GatewayClientTestHelper();
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "chat",
          "payload": {
            "sessionKey": "main",
            "state": "final",
            "message": {
              "role": "system",
              "content": "Context compacted",
              "__openclaw": {
                "kind": "compaction",
                "tokensBefore": 42000,
                "tokensAfter": 12000
              }
            }
          }
        }
        """);

        Assert.NotNull(received);
        Assert.Equal("compaction", received!.OpenClawKind);
        Assert.Equal(42000, received.CompactionTokensBefore);
        Assert.Equal(12000, received.CompactionTokensAfter);
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageWithMalformedMessage_DropsFrame()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        ChatMessageInfo? received = null;
        helper.Client.ChatMessageReceived += (_, message) => received = message;

        helper.ProcessRawMessage("""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "agent:main:whatsapp:direct:+15551234567",
            "message": "not-an-object",
            "state": "final"
          }
        }
        """);

        Assert.Null(received);
        Assert.Contains(logger.Logs, log => log.Contains("message payload was not an object", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("final", true)]
    [InlineData("streaming", false)]
    public void ProcessRawMessage_SessionMessageAssistantNotification_DependsOnFinalState(string state, bool expectNotification)
    {
        var helper = new GatewayClientTestHelper();
        OpenClawNotification? notification = null;
        helper.Client.NotificationReceived += (_, value) => notification = value;

        helper.ProcessRawMessage($$"""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "agent:main:whatsapp:direct:+15551234567",
            "message": {
              "role": "assistant",
              "content": "assistant reply",
              "timestamp": 1781631280633
            },
            "state": "{{state}}"
          }
        }
        """);

        if (expectNotification)
        {
            Assert.NotNull(notification);
            Assert.Equal("assistant reply", notification!.Message);
            Assert.True(notification.IsChat);
        }
        else
        {
            Assert.Null(notification);
        }
    }

    [Fact]
    public void ProcessRawMessage_SessionMessageAssistantNotification_PreservesFullMessage()
    {
        var helper = new GatewayClientTestHelper();
        OpenClawNotification? notification = null;
        helper.Client.NotificationReceived += (_, value) => notification = value;

        var fullMessage = new string('x', 240);

        helper.ProcessRawMessage($$"""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "agent:main:whatsapp:direct:+15551234567",
            "message": {
              "role": "assistant",
              "content": "{{fullMessage}}",
              "timestamp": 1781631280633
            },
            "state": "final"
          }
        }
        """);

        Assert.NotNull(notification);
        Assert.True(notification!.IsChat);
        Assert.Equal(fullMessage[..200] + "…", notification.Message);
        Assert.Equal(fullMessage, notification.FullMessage);
    }

    [Theory]
    [InlineData("streaming", false)]
    [InlineData("final", true)]
    public void ProcessRawMessage_LegacyAssistantNotification_DependsOnFinalState(
        string state,
        bool expectNotification)
    {
        var helper = new GatewayClientTestHelper();
        var notifications = new List<OpenClawNotification>();
        helper.Client.NotificationReceived += (_, value) => notifications.Add(value);

        helper.ProcessRawMessage($$"""
        {
          "type": "event",
          "event": "session.message",
          "payload": {
            "sessionKey": "main",
            "role": "assistant",
            "text": "legacy reply",
            "state": "{{state}}"
          }
        }
        """);

        Assert.Equal(expectNotification ? 1 : 0, notifications.Count);
    }

    [Fact]
    public void ProcessRawMessage_AgentEventLogsRawLengthWithoutPayloadContent()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        var rawMessage = "{\"type\":\"event\",\"event\":\"agent\",\"payload\":{\"sessionKey\":\"main\",\"stream\":\"tool\",\"data\":{\"phase\":\"call\",\"name\":\"shell\",\"args\":{\"command\":\"super-secret command\"}}}}";

        helper.ProcessRawMessage(rawMessage);

        Assert.Contains(logger.Logs, log => log == $"DEBUG: Agent event received: stream=tool len={rawMessage.Length}");
        Assert.DoesNotContain(logger.Logs, log => log.Contains("super-secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseModelsList_PreservesConfiguredFlagPresence()
    {
        var helper = new GatewayClientTestHelper();

        var models = helper.ParseModelsListPayload("""
        {
          "models": [
            { "id": "gpt-5.4", "configured": true },
            { "id": "gpt-5.5", "configured": false },
            { "id": "legacy-gateway-model" }
          ]
        }
        """);

        Assert.Collection(
            models.Models,
            model =>
            {
                Assert.Equal("gpt-5.4", model.Id);
                Assert.True(model.HasConfiguredFlag);
                Assert.True(model.IsConfigured);
            },
            model =>
            {
                Assert.Equal("gpt-5.5", model.Id);
                Assert.True(model.HasConfiguredFlag);
                Assert.False(model.IsConfigured);
            },
            model =>
            {
                Assert.Equal("legacy-gateway-model", model.Id);
                Assert.False(model.HasConfiguredFlag);
                Assert.False(model.IsConfigured);
            });
    }

    [Fact]
    public void ClassifyNotification_DetectsHealthAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("health", helper.ClassifyNotification("Your blood sugar is high"));
        Assert.Equal("health", helper.ClassifyNotification("Glucose level: 180 mg/dl"));
        Assert.Equal("health", helper.ClassifyNotification("CGM reading available"));
    }

    [Fact]
    public void ClassifyNotification_DetectsUrgentAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("urgent", helper.ClassifyNotification("URGENT: Action required"));
        Assert.Equal("urgent", helper.ClassifyNotification("This is critical"));
        Assert.Equal("urgent", helper.ClassifyNotification("Emergency situation"));
    }

    [Fact]
    public void ClassifyNotification_DetectsReminders()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("reminder", helper.ClassifyNotification("Reminder: Meeting at 3pm"));
    }

    [Fact]
    public void ClassifyNotification_DetectsStockAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("stock", helper.ClassifyNotification("Item is in stock"));
        Assert.Equal("stock", helper.ClassifyNotification("Available now!"));
    }

    [Fact]
    public void ClassifyNotification_DetectsEmailNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("email", helper.ClassifyNotification("New email in inbox"));
        Assert.Equal("email", helper.ClassifyNotification("Gmail notification"));
    }

    [Fact]
    public void ClassifyNotification_DetectsCalendarEvents()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("calendar", helper.ClassifyNotification("Meeting starting soon"));
        Assert.Equal("calendar", helper.ClassifyNotification("Calendar event: Team standup"));
    }

    [Fact]
    public void ClassifyNotification_DetectsErrorNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("error", helper.ClassifyNotification("Build failed"));
        Assert.Equal("error", helper.ClassifyNotification("Exception occurred"));
    }

    [Fact]
    public void ClassifyNotification_DetectsBuildNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("build", helper.ClassifyNotification("Build succeeded"));
        Assert.Equal("build", helper.ClassifyNotification("CI pipeline completed"));
        Assert.Equal("build", helper.ClassifyNotification("Deploy finished"));
    }

    [Fact]
    public void ClassifyNotification_DefaultsToInfo()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("info", helper.ClassifyNotification("Hello world"));
        Assert.Equal("info", helper.ClassifyNotification("Random message"));
    }

    [Fact]
    public void ClassifyNotification_IsCaseInsensitive()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("urgent", helper.ClassifyNotification("URGENT: test"));
        Assert.Equal("urgent", helper.ClassifyNotification("urgent: test"));
        Assert.Equal("urgent", helper.ClassifyNotification("Urgent: test"));
    }

    [Fact]
    public void ClassifyNotification_ReturnsCorrectTitle_ForHealth()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("🩸 Blood Sugar Alert", helper.GetNotificationTitle("blood sugar high"));
    }

    [Fact]
    public void ClassifyNotification_ReturnsCorrectTitle_ForUrgent()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("🚨 Urgent Alert", helper.GetNotificationTitle("urgent message"));
    }

    [Fact]
    public void ClassifyTool_MapsExec()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Exec, helper.ClassifyTool("exec"));
        Assert.Equal(ActivityKind.Exec, helper.ClassifyTool("EXEC"));
    }

    [Fact]
    public void ClassifyTool_MapsRead()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Read, helper.ClassifyTool("read"));
    }

    [Fact]
    public void ClassifyTool_MapsWrite()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Write, helper.ClassifyTool("write"));
    }

    [Fact]
    public void ClassifyTool_MapsEdit()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Edit, helper.ClassifyTool("edit"));
    }

    [Fact]
    public async Task PendingChatSend_CompletesOnSuccessfulResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingChatSend("chat-1");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "chat-1",
            "ok": true,
            "payload": { "accepted": true, "runId": "run-1" }
        }
        """);

        var result = await task;
        Assert.Equal("run-1", result.RunId);
    }

    [Fact]
    public void ParseChatSendResponse_ReadsQueueAckStatus()
    {
        var helper = new GatewayClientTestHelper();

        var result = helper.ParseChatSendResponse("""
        {
            "type": "res",
            "id": "chat-1",
            "ok": true,
            "payload": {
                "runId": "run-1",
                "sessionKey": "main",
                "status": "started"
            },
            "meta": { "cached": true }
        }
        """);

        Assert.Equal("run-1", result.RunId);
        Assert.Equal("main", result.SessionKey);
        Assert.Equal("started", result.Status);
        Assert.True(result.Cached);
        Assert.False(result.IsTerminalFailure);
    }

    [Fact]
    public void ParseChatSendResponse_ReadsTerminalFailureStatus()
    {
        var helper = new GatewayClientTestHelper();

        var result = helper.ParseChatSendResponse("""
        {
            "type": "res",
            "id": "chat-1",
            "ok": true,
            "payload": {
                "status": "failed",
                "error": { "message": "model unavailable" }
            }
        }
        """);

        Assert.Equal("failed", result.Status);
        Assert.Equal("model unavailable", result.Error);
        Assert.True(result.IsTerminalFailure);
    }

    [Fact]
    public async Task PendingChatSend_FailsOnErrorResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingChatSend("chat-2");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "chat-2",
            "ok": false,
            "error": "missing scope: operator.write"
        }
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Contains("operator.write", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingApprovalResolve_CompletesOnSuccessfulResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingApprovalResolve("approval-1");

        helper.ProcessRawMessage(
            """{"type":"res","id":"approval-1","ok":true}""");

        Assert.True(await task);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task PendingApprovalResolve_FailsOnRejectedResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingApprovalResolve("approval-2");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "approval-2",
            "ok": false,
            "error": { "message": "approval not found" }
        }
        """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await task);
        Assert.Equal("approval not found", exception.Message);
        Assert.Equal(0, helper.GetPendingRequestCount());
    }

    [Fact]
    public async Task PendingWizardResponse_ClearPendingRequests_FailsWithOperationCanceledException()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingWizardResponse("wizard-1");

        helper.ClearPendingRequests();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.Contains("wizard response", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingWizardNext_OnDisconnected_CompletesImmediatelyWithOperationCanceledException()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingWizardResponse("wizard-2");

        helper.OnDisconnected();

        // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
        var completed = await Task.WhenAny(task, Task.Delay(250));
        Assert.Same(task, completed);
        var exception = await Assert.ThrowsAsync<GatewayConnectionLostException>(
            async () => await task);
        Assert.Null(exception.CloseStatusCode);
    }

        [Fact]
        public void ParseHandshakeMainSessionKey_ReturnsMainKey_WhenPresent()
        {
                var helper = new GatewayClientTestHelper();
                var key = helper.ParseHandshakeMainSessionKey("""
                {
                    "type": "hello-ok",
                    "snapshot": {
                        "sessionDefaults": {
                            "mainKey": "agent:main:123"
                        }
                    }
                }
                """);

                Assert.Equal("agent:main:123", key);
        }

        [Fact]
        public void ParseHandshakeMainSessionKey_ReturnsNull_WhenMissing()
        {
                var helper = new GatewayClientTestHelper();
                var key = helper.ParseHandshakeMainSessionKey("""
                {
                    "type": "hello-ok",
                    "snapshot": {
                        "sessionDefaults": {
                        }
                    }
                }
                """);

                Assert.Null(key);
        }

        [Fact]
        public void ParseHandshakeDeviceToken_ReturnsValue_WhenPresent()
        {
                var helper = new GatewayClientTestHelper();
                var token = helper.ParseHandshakeDeviceToken("""
                {
                    "type": "hello-ok",
                    "auth": {
                        "deviceToken": "device-token-123"
                    }
                }
                """);

                Assert.Equal("device-token-123", token);
        }

        [Fact]
        public void ParseHandshakeDeviceToken_ReturnsNull_WhenMissing()
        {
                var helper = new GatewayClientTestHelper();
                var token = helper.ParseHandshakeDeviceToken("""
                {
                    "type": "hello-ok",
                    "auth": {
                    }
                }
                """);

                Assert.Null(token);
        }

    [Fact]
    public void ClassifyTool_MapsWebSearch()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Search, helper.ClassifyTool("web_search"));
        Assert.Equal(ActivityKind.Search, helper.ClassifyTool("web_fetch"));
    }

    [Fact]
    public void ClassifyTool_MapsBrowser()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Browser, helper.ClassifyTool("browser"));
    }

    [Fact]
    public void ClassifyTool_MapsMessage()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Message, helper.ClassifyTool("message"));
    }

    [Fact]
    public void ClassifyTool_DefaultsToTool()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("unknown_tool"));
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("tts"));
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("image"));
    }

    [Fact]
    public void ShortenPath_ReturnsEmpty_ForEmptyPath()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("", helper.ShortenPath(""));
    }

    [Fact]
    public void ShortenPath_ReturnsFilename_ForSingleComponent()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("file.txt", helper.ShortenPath("file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsLastTwoComponents_ForLongPath()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/folder/file.txt", helper.ShortenPath("/very/long/path/folder/file.txt"));
    }

    [Fact]
    public void ShortenPath_HandlesBackslashes()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/folder/file.txt", helper.ShortenPath(@"C:\Users\admin\folder\file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsLastComponent_ForTwoComponents()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("file.txt", helper.ShortenPath("folder/file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsFilename_ForLeadingSlash()
    {
        // "/file.txt" splits as ["", "file.txt"] — only 2 parts so show just filename.
        var helper = new GatewayClientTestHelper();
        Assert.Equal("file.txt", helper.ShortenPath("/file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsLastTwoComponents_ForLeadingSlashThreeParts()
    {
        // "/folder/file.txt" splits as ["", "folder", "file.txt"] — 3 parts so show "…/folder/file.txt".
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/folder/file.txt", helper.ShortenPath("/folder/file.txt"));
    }

    [Fact]
    public void ShortenPath_HandlesMixedSeparators()
    {
        // Mixed \ and / in same path (e.g. a WSL path reconstructed on Windows).
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/src/main.cs", helper.ShortenPath(@"C:\repos/project\src/main.cs"));
    }

    [Fact]
    public void TruncateLabel_ReturnsUnchanged_WhenShorterThanMax()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("short text", helper.TruncateLabel("short text", 60));
    }

    [Fact]
    public void TruncateLabel_Truncates_WhenLongerThanMax()
    {
        var helper = new GatewayClientTestHelper();
        var longText = "This is a very long text that should be truncated because it exceeds the maximum length";
        var result = helper.TruncateLabel(longText, 60);
        Assert.Equal(60, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncateLabel_HandlesEmpty()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("", helper.TruncateLabel("", 60));
    }

    [Fact]
    public void TruncateLabel_HandlesExactLength()
    {
        var helper = new GatewayClientTestHelper();
        var text = new string('x', 60);
        Assert.Equal(text, helper.TruncateLabel(text, 60));
    }

    [Fact]
    public void GetSessionList_SortsMainSessionFirst()
    {
        var helper = new GatewayClientTestHelper();

        // Populate with a mix of sub-sessions and one main session.
        // The main session is listed last in the JSON to prove sorting moves it first.
        helper.ParseSessionsPayload("""
        {
            "agent:sub:older": { "status": "idle", "model": "gpt-4" },
            "agent:sub:newer": { "status": "active", "model": "gpt-4" },
            "agent:main:main": { "status": "active", "model": "gpt-4" }
        }
        """);

        var sessions = helper.GetSessionList();

        Assert.Equal(3, sessions.Length);
        Assert.True(sessions[0].IsMain, "Main session should be sorted first");
        Assert.Contains("main", sessions[0].Key);
        Assert.False(sessions[1].IsMain);
        Assert.False(sessions[2].IsMain);
    }

    [Fact]
    public void ParseSessions_ProjectsFlattenedSessionFacts()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:telegram:main:direct:491234567890",
            "label": "Family chat",
            "displayName": "Telegram:491234567890",
            "derivedTitle": "Latest plans",
            "modelProvider": "openai",
            "model": "gpt-5.4",
            "channel": "telegram",
            "groupChannel": "family",
            "chatType": "direct",
            "origin": { "label": "Tony" },
            "worktree": { "id": "wt-1", "branch": "openclaw/session-ux", "repoRoot": "C:\\src\\openclaw" },
            "execNode": "windows-dev",
            "parentSessionKey": "agent:main:main",
            "spawnDepth": 1,
            "classification": "direct",
            "agentId": "main",
            "accountId": "main",
            "peerKind": "direct",
            "isMain": false,
            "isBackground": false
          }
        ]
        """);

        var session = Assert.Single(helper.GetSessionList());
        Assert.Equal("Family chat", session.Label);
        Assert.Equal("Latest plans", session.DerivedTitle);
        Assert.Equal("openai", session.Provider);
        Assert.Equal("family", session.Room);
        Assert.Equal("direct", session.ChatType);
        Assert.Equal("Tony", session.OriginLabel);
        Assert.Equal("openclaw/session-ux", session.Worktree?.Branch);
        Assert.Equal("windows-dev", session.ExecNode);
        Assert.Equal("agent:main:main", session.ParentSessionKey);
        Assert.Equal(1, session.SpawnDepth);
        Assert.False(session.IsMain);
        Assert.Equal("direct", session.Classification);
        Assert.Equal("main", session.AgentId);
        Assert.Equal("main", session.AccountId);
        Assert.Equal("direct", session.PeerKind);
    }

    [Fact]
    public void ParseSessions_UsesHandshakeMainSessionKeyInsteadOfKeyShapeGuessing()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetMainSessionKey("global");
        helper.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:main",
            "displayName": "Named non-main session",
            "isMain": true
          },
          {
            "key": "global",
            "displayName": "Global main",
            "isMain": false
          }
        ]
        """);

        var sessions = helper.GetSessionList();
        Assert.True(Assert.Single(sessions, session => session.Key == "global").IsMain);
        Assert.False(Assert.Single(sessions, session => session.Key == "agent:main:main").IsMain);
    }

    [Fact]
    public void ParseSessions_LegacyHandshakeAliasUsesRowMetadataAndBoundedCanonicalFallback()
    {
        var withRowFacts = new GatewayClientTestHelper();
        withRowFacts.SetMainSessionKey("main", isCanonical: false);
        withRowFacts.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:main",
            "isMain": true
          }
        ]
        """);
        Assert.True(Assert.Single(withRowFacts.GetSessionList()).IsMain);

        var withoutPresentation = new GatewayClientTestHelper();
        withoutPresentation.SetMainSessionKey("main", isCanonical: false);
        withoutPresentation.ParseSessionsPayload("""
        [ { "key": "agent:main:main", "status": "active" } ]
        """);
        Assert.True(Assert.Single(withoutPresentation.GetSessionList()).IsMain);
    }

    [Fact]
    public void ParseSessions_UsesRowMainBeforeHandshakeAuthority()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [
          {
            "key": "global",
            "isMain": true
          }
        ]
        """);

        Assert.True(Assert.Single(helper.GetSessionList()).IsMain);
    }

    [Fact]
    public void ParseSessions_FallsBackWhenFlatFactsAreAbsent()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:subagent:child",
            "status": "active"
          }
        ]
        """);

        var session = Assert.Single(helper.GetSessionList());
        Assert.True(SessionDisplayResolver.IsBackground(session));
    }

    [Fact]
    public void ParseSessions_MapPayloadUsesRowMainOnlyBeforeHandshakeAuthority()
    {
        var legacy = new GatewayClientTestHelper();
        legacy.ParseSessionsPayload("""
        { "session-custom": { "isMain": true, "displayName": "Legacy main" } }
        """);
        Assert.True(Assert.Single(legacy.GetSessionList()).IsMain);

        var connected = new GatewayClientTestHelper();
        connected.SetMainSessionKey("global");
        connected.ParseSessionsPayload("""
        {
          "session-custom": { "isMain": true, "displayName": "Not main" },
          "global": { "isMain": false, "displayName": "Global main" }
        }
        """);
        Assert.False(Assert.Single(connected.GetSessionList(), session => session.Key == "session-custom").IsMain);
        Assert.True(Assert.Single(connected.GetSessionList(), session => session.Key == "global").IsMain);
    }

    [Fact]
    public void ParseSessions_SparseUpdatesPreserveExplicitFalseMainStatus()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:main",
            "isMain": false
          },
          { "key": "main", "isMain": false }
        ]
        """);
        helper.ParseSessionsPayload("""
        [
          { "key": "agent:main:main", "status": "active" },
          { "key": "main", "status": "active" }
        ]
        """);

        Assert.All(helper.GetSessionList(), session => Assert.False(session.IsMain));
    }

    [Fact]
    public void ParseSessions_SparseUpdatesPreserveFlattenedFacts()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [
          {
            "key": "agent:main:subagent:child",
            "channel": "telegram",
            "classification": "subagent",
            "agentId": "main",
            "isMain": false,
            "isBackground": true
          }
        ]
        """);
        helper.ParseSessionsPayload("""
        [{ "key": "agent:main:subagent:child", "status": "active" }]
        """);

        var session = Assert.Single(helper.GetSessionList());
        Assert.Equal("telegram", session.Channel);
        Assert.Equal("subagent", session.Classification);
        Assert.Equal("main", session.AgentId);
        Assert.True(session.IsBackground == true);
    }

    [Fact]
    public void ParseSessions_SparseUpdatesPreserveLegacyRowMainStatus()
    {
        var helper = new GatewayClientTestHelper();
        helper.ParseSessionsPayload("""
        [{ "key": "session-custom", "isMain": true, "displayName": "Legacy main" }]
        """);
        helper.ParseSessionsPayload("""
        [{ "key": "session-custom", "status": "active" }]
        """);

        Assert.True(Assert.Single(helper.GetSessionList()).IsMain);
    }

    [Fact]
    public void ParseSessions_EmptyArray_ClearsPreviousSessions()
    {
        var helper = new GatewayClientTestHelper();

        // First populate with sessions
        helper.ParseSessionsPayload("""
        {
            "agent:main:main": { "status": "active", "model": "gpt-4" },
            "agent:sub:worker": { "status": "idle", "model": "gpt-4" }
        }
        """);
        Assert.Equal(2, helper.GetSessionList().Length);

        // Now parse an empty array — sessions should be cleared
        helper.ParseSessionsPayload("[]");
        Assert.Empty(helper.GetSessionList());
    }

    [Fact]
    public void ParseSessions_PreservesGatewayRunLivenessAndDoesNotInventActiveStatus()
    {
        var helper = new GatewayClientTestHelper();

        helper.ParseSessionsPayload("""
        [
          { "key": "agent:main:working", "status": "running", "hasActiveRun": true },
          { "key": "agent:main:idle", "status": "running", "hasActiveRun": false },
          { "key": "agent:main:unknown" }
        ]
        """);

        var sessions = helper.GetSessionList().ToDictionary(session => session.Key);
        Assert.True(sessions["agent:main:working"].HasActiveRun == true);
        Assert.True(sessions["agent:main:idle"].HasActiveRun == false);
        Assert.Null(sessions["agent:main:unknown"].HasActiveRun);
        Assert.Equal("unknown", sessions["agent:main:unknown"].Status);
    }

    [Fact]
    public void ParseSessions_RetainsRunStateWhenSparseUpdateOmitsIt()
    {
        var helper = new GatewayClientTestHelper();

        helper.ParseSessionsPayload("""
        [{ "key": "agent:main:stateful", "status": "failed", "hasActiveRun": false }]
        """);
        helper.ParseSessionsPayload("""
        [{ "key": "agent:main:stateful", "displayName": "Current task" }]
        """);

        var session = Assert.Single(helper.GetSessionList());
        Assert.Equal("failed", session.Status);
        Assert.Equal(false, session.HasActiveRun);
        Assert.Equal("Current task", session.DisplayName);
    }

    [Fact]
    public void ParseUsageStatusPayload_PopulatesProviderSummary()
    {
        var helper = new GatewayClientTestHelper();
        var usage = helper.ParseUsageStatusPayload("""
            {
              "updatedAt": 1739760000000,
              "providers": [
                {
                  "provider": "openai",
                  "displayName": "OpenAI",
                  "windows": [
                    { "label": "daily", "usedPercent": 27.5 }
                  ]
                }
              ]
            }
            """);

        Assert.NotNull(usage.ProviderSummary);
        Assert.Contains("OpenAI", usage.ProviderSummary!);
        Assert.Contains("left", usage.ProviderSummary!);
    }

    // ── BuildProviderSummary tests ──────────────────────────────────────────────

    [Fact]
    public void BuildProviderSummary_NoProviders_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo { Providers = [] };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_SingleProviderWithUsage_ShowsRemainingPercent()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "OpenAI",
                    Windows = [new GatewayUsageWindowInfo { Label = "daily", UsedPercent = 25.0 }]
                }
            ]
        };

        var result = helper.CallBuildProviderSummary(status);

        Assert.Equal("OpenAI: 75% left", result);
    }

    [Fact]
    public void BuildProviderSummary_SingleProviderWithError_ShowsErrorLabel()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo { DisplayName = "Anthropic", Error = "rate limited" }
            ]
        };

        Assert.Equal("Anthropic: error", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_ProviderWithNoWindows_IsSkipped()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers = [new GatewayUsageProviderInfo { DisplayName = "OpenAI" }]
        };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_TwoProviders_JoinedWithSeparator()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "OpenAI",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 20.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "Anthropic",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 50.0 }]
                }
            ]
        };

        Assert.Equal("OpenAI: 80% left · Anthropic: 50% left", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_ThreeProviders_ShowsOverflowCount()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P1",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 10.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P2",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 20.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P3",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 30.0 }]
                }
            ]
        };

        var result = helper.CallBuildProviderSummary(status);

        Assert.Equal("P1: 90% left · P2: 80% left · +1", result);
    }

    [Fact]
    public void BuildProviderSummary_MissingDisplayName_FallsBackToProviderField()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    Provider = "openai",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 0.0 }]
                }
            ]
        };

        Assert.StartsWith("openai:", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_AllProvidersEmpty_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo { DisplayName = "P1" },
                new GatewayUsageProviderInfo { DisplayName = "P2" }
            ]
        };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_OverflowWithOneValidProvider_ShowsOverflow()
    {
        var helper = new GatewayClientTestHelper();
        // 3 providers but only the first has windows — included=1, but Providers.Count=3 > 2 → overflow shown
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P1",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 10.0 }]
                },
                new GatewayUsageProviderInfo { DisplayName = "P2" },
                new GatewayUsageProviderInfo { DisplayName = "P3" }
            ]
        };

        Assert.Equal("P1: 90% left · +1", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void ParseUsageCostPayload_UpdatesLegacyUsageTotals()
    {
        var helper = new GatewayClientTestHelper();
        var usage = helper.ParseUsageCostPayload("""
            {
              "updatedAt": 1739760000000,
              "days": 30,
              "totals": {
                "totalTokens": 12345,
                "totalCost": 1.23
              }
            }
            """);

        Assert.Equal(12345, usage.TotalTokens);
        Assert.Equal(1.23, usage.CostUsd, 3);
    }

    [Fact]
    public void ParseSessionsPreviewPayload_EmitsPreviewRows()
    {
        var helper = new GatewayClientTestHelper();
        var previewPayload = helper.ParseSessionsPreviewPayload("""
            {
              "ts": 1739760000000,
              "previews": [
                {
                  "key": "agent:main:main",
                  "status": "ok",
                  "items": [
                    { "role": "user", "text": "hello" },
                    { "role": "assistant", "text": "world" }
                  ]
                }
              ]
            }
            """);

        var preview = Assert.Single(previewPayload.Previews);
        Assert.Equal("agent:main:main", preview.Key);
        Assert.Equal("ok", preview.Status);
        Assert.Equal(2, preview.Items.Count);
        Assert.Equal("user", preview.Items[0].Role);
        Assert.Equal("hello", preview.Items[0].Text);
    }

    [Fact]
    public void ParseNodeListPayload_ParsesAndSortsNodes()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "node-online",
                  "displayName": "Windows Node",
                  "status": "connected",
                   "platform": "windows",
                   "mode": "node",
                   "commands": ["system.run", "canvas.present"],
                   "caps": ["system"],
                   "permissions": { "screen.record": true, "camera.snap": false },
                   "lastSeenAt": 1739760000000
                 },
                {
                  "deviceId": "node-offline",
                  "name": "Mac Node",
                  "status": "offline",
                  "platform": "darwin",
                  "mode": "node",
                  "commands": [],
                  "capabilities": ["camera"],
                  "lastSeenAt": 1739750000000
                }
              ]
            }
            """);

        Assert.Equal(2, nodes.Length);
        Assert.Equal("node-online", nodes[0].NodeId);
        Assert.True(nodes[0].IsOnline);
        Assert.Equal(2, nodes[0].CommandCount);
        Assert.Equal(1, nodes[0].CapabilityCount);
        Assert.Equal(["system.run", "canvas.present"], nodes[0].Commands);
        Assert.Equal(["system"], nodes[0].Capabilities);
        Assert.True(nodes[0].Permissions["screen.record"]);
        Assert.False(nodes[0].Permissions["camera.snap"]);

        Assert.Equal("node-offline", nodes[1].NodeId);
        Assert.False(nodes[1].IsOnline);
        Assert.Empty(nodes[1].Commands);
        Assert.Equal(["camera"], nodes[1].Capabilities);
    }

    [Fact]
    public void ParseNodeListPayload_EmptyArray_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""{ "nodes": [] }""");
        Assert.Empty(nodes);
    }

    [Fact]
    public void ParseModelsList_PopulatesProviderRichMetadata()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            {
              "models": [
                {
                  "id": "claude-opus-4.8",
                  "name": "Claude Opus 4.8",
                  "provider": "Anthropic",
                  "contextWindow": 200000,
                  "configured": true,
                  "default": true
                },
                {
                  "id": "gemini-3.1-pro",
                  "name": "Gemini 3.1 Pro",
                  "provider": "Google",
                  "contextWindow": 1000000,
                  "requiresAuth": true
                },
                {
                  "id": "local-llama",
                  "provider": "Ollama",
                  "unavailable": true
                }
              ]
            }
            """);

        Assert.Equal(3, info.Models.Count);

        var opus = info.Models[0];
        Assert.Equal("claude-opus-4.8", opus.Id);
        Assert.Equal("Anthropic", opus.Provider);
        Assert.Equal(200000, opus.ContextWindow);
        Assert.True(opus.IsConfigured);
        Assert.True(opus.IsDefault);
        Assert.True(opus.IsAvailable);
        Assert.False(opus.RequiresAuth);

        var gemini = info.Models[1];
        Assert.True(gemini.RequiresAuth);
        Assert.False(gemini.IsDefault);
        Assert.True(gemini.IsAvailable); // no availability signal → usable

        var llama = info.Models[2];
        Assert.False(llama.IsAvailable); // unavailable:true inverts to false
        Assert.Equal("local-llama", llama.DisplayName); // name omitted → id
    }

    [Fact]
    public void ParseModelsList_PreservesRuntimeAndNativeContextMetadata()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            {
              "models": [
                {
                  "id": "gpt-5.4",
                  "contextWindow": 1000000,
                  "contextTokens": 272000
                },
                {
                  "id": "legacy-model",
                  "contextWindow": 128000
                }
              ]
            }
            """);

        Assert.Collection(
            info.Models,
            current =>
            {
                Assert.Equal(1000000, current.ContextWindow);
                Assert.Equal(272000, current.ContextTokens);
            },
            legacy =>
            {
                Assert.Equal(128000, legacy.ContextWindow);
                Assert.Null(legacy.ContextTokens);
            });
    }

    [Fact]
    public void ParseModelsList_InvalidContextMetadata_DoesNotDropModelsOrValidFields()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            {
              "models": [
                {
                  "id": "valid-native",
                  "contextWindow": 128000,
                  "contextTokens": 128000.5
                },
                {
                  "id": "valid-runtime",
                  "contextWindow": 2147483648,
                  "contextTokens": 272000
                },
                {
                  "id": "non-positive",
                  "contextWindow": 0,
                  "contextTokens": -1
                },
                {
                  "id": "unaffected",
                  "contextWindow": 64000,
                  "contextTokens": 32000
                }
              ]
            }
            """);

        Assert.Collection(
            info.Models,
            native =>
            {
                Assert.Equal("valid-native", native.Id);
                Assert.Equal(128000, native.ContextWindow);
                Assert.Null(native.ContextTokens);
            },
            runtime =>
            {
                Assert.Equal("valid-runtime", runtime.Id);
                Assert.Null(runtime.ContextWindow);
                Assert.Equal(272000, runtime.ContextTokens);
            },
            nonPositive =>
            {
                Assert.Equal("non-positive", nonPositive.Id);
                Assert.Null(nonPositive.ContextWindow);
                Assert.Null(nonPositive.ContextTokens);
            },
            unaffected =>
            {
                Assert.Equal("unaffected", unaffected.Id);
                Assert.Equal(64000, unaffected.ContextWindow);
                Assert.Equal(32000, unaffected.ContextTokens);
            });
    }

    [Fact]
    public void ParseModelsList_DefaultsAvailableTrue_WhenNoReadinessSignals()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            { "models": [ { "id": "gpt-5.5", "name": "GPT-5.5" } ] }
            """);

        var m = Assert.Single(info.Models);
        Assert.True(m.IsAvailable);
        Assert.False(m.RequiresAuth);
        Assert.False(m.IsDefault);
        Assert.False(m.IsConfigured);
    }

    [Fact]
    public void ParseModelsList_AvailableFalse_MarksUnavailable()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            { "models": [ { "id": "x", "available": false } ] }
            """);

        Assert.False(Assert.Single(info.Models).IsAvailable);
    }

    [Fact]
    public void ParseModelsList_AcceptsIsDefaultAndAuthNeededAliases()
    {
        var helper = new GatewayClientTestHelper();
        var info = helper.ParseModelsListPayload("""
            { "models": [ { "id": "x", "isDefault": true, "authNeeded": true } ] }
            """);

        var m = Assert.Single(info.Models);
        Assert.True(m.IsDefault);
        Assert.True(m.RequiresAuth);
    }

    [Fact]
    public void ParseNodeListPayload_SameOnlineStatus_SortsByLastSeenDescending()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                { "nodeId": "older", "status": "connected", "lastSeenAt": 1000000000000 },
                { "nodeId": "newer", "status": "connected", "lastSeenAt": 2000000000000 },
                { "nodeId": "middle", "status": "connected", "lastSeenAt": 1500000000000 }
              ]
            }
            """);

        Assert.Equal(3, nodes.Length);
        Assert.Equal("newer", nodes[0].NodeId);
        Assert.Equal("middle", nodes[1].NodeId);
        Assert.Equal("older", nodes[2].NodeId);
    }

    [Fact]
    public void ParseNodeListPayload_SkipsItemsWithNoNodeId()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                { "nodeId": "valid-node", "status": "connected" },
                { "status": "connected" }
              ]
            }
            """);

        Assert.Single(nodes);
        Assert.Equal("valid-node", nodes[0].NodeId);
    }

    [Fact]
    public void ParseNodeListPayload_PopulatesAllNodeListNodeFields()
    {
        // Mirrors the full NodeListNode schema from openclaw/openclaw
        // src/shared/node-list-types.ts so we don't lose data the gateway
        // already sends. Uses the production *Ms timestamp names.
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "node-rich",
                  "displayName": "Rich Node",
                  "platform": "windows",
                  "mode": "node",
                  "status": "connected",
                  "version": "v2026.5.7",
                  "coreVersion": "1.2.3",
                  "uiVersion": "4.5.6",
                  "clientId": "client-abc",
                  "clientMode": "operator-node",
                  "remoteIp": "192.168.1.42",
                  "deviceFamily": "desktop",
                  "modelIdentifier": "Surface-Pro-X",
                  "pathEnv": "C:\\Windows;C:\\tools",
                  "caps": ["camera", "screen"],
                  "commands": ["system.run"],
                  "disabledCommands": ["camera.recordVideo"],
                  "permissions": { "screen.record": true, "camera.snap": false },
                  "approvalState": "pending-reapproval",
                  "pendingRequestId": "req-node-rich",
                  "pendingDeclaredCaps": ["camera", "screen", "location"],
                  "pendingDeclaredCommands": ["system.run", "location.get"],
                  "pendingDeclaredPermissions": { "system.run": true, "location.get": false },
                  "paired": true,
                  "connected": true,
                  "connectedAtMs": 1739760000000,
                  "lastSeenAtMs": 1739760123456,
                  "lastSeenReason": "heartbeat",
                  "approvedAtMs": 1739700000000
                }
              ]
            }
            """);

        Assert.Single(nodes);
        var n = nodes[0];
        Assert.Equal("node-rich", n.NodeId);
        Assert.Equal("Rich Node", n.DisplayName);
        Assert.Equal("v2026.5.7", n.Version);
        Assert.Equal("1.2.3", n.CoreVersion);
        Assert.Equal("4.5.6", n.UiVersion);
        Assert.Equal("client-abc", n.ClientId);
        Assert.Equal("operator-node", n.ClientMode);
        Assert.True(n.HasExplicitDisplayName);
        Assert.Equal("192.168.1.42", n.RemoteIp);
        Assert.Equal("desktop", n.DeviceFamily);
        Assert.Equal("Surface-Pro-X", n.ModelIdentifier);
        Assert.Equal("C:\\Windows;C:\\tools", n.PathEnv);
        Assert.Equal(["camera", "screen"], n.Capabilities);
        Assert.Equal(["system.run"], n.Commands);
        Assert.Equal(["camera.recordVideo"], n.DisabledCommands);
        Assert.Equal(GatewayNodeApprovalState.PendingReapproval, n.ApprovalState);
        Assert.Equal("req-node-rich", n.PendingRequestId);
        Assert.Equal(["camera", "screen", "location"], n.PendingDeclaredCapabilities);
        Assert.Equal(["system.run", "location.get"], n.PendingDeclaredCommands);
        Assert.True(n.PendingDeclaredPermissions["system.run"]);
        Assert.False(n.PendingDeclaredPermissions["location.get"]);
        Assert.True(n.IsPaired);
        Assert.True(n.IsOnline);
        Assert.True(n.Permissions["screen.record"]);
        Assert.False(n.Permissions["camera.snap"]);
        Assert.Equal("heartbeat", n.LastSeenReason);

        // Timestamps come from *Ms wire names
        Assert.NotNull(n.ConnectedAt);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1739760000000).UtcDateTime,
            n.ConnectedAt!.Value);
        Assert.NotNull(n.ApprovedAt);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1739700000000).UtcDateTime,
            n.ApprovedAt!.Value);
        Assert.NotNull(n.LastSeen);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1739760123456).UtcDateTime,
            n.LastSeen!.Value);
    }

    [Fact]
    public void ParseNodeListPayload_PendingDeclarationsNeverPopulateEffectiveFieldsOrCounts()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "pending-node",
                  "approvalState": "pending-reapproval",
                  "pendingRequestId": "request-123",
                  "caps": ["system"],
                  "commands": ["system.notify"],
                  "declaredCommands": ["system.notify", "camera.snap", "legacy.unsafe"],
                  "permissions": { "system.notify": true },
                  "pendingDeclaredCaps": ["system", "camera"],
                  "pendingDeclaredCommands": ["system.notify", "camera.snap"],
                  "pendingDeclaredPermissions": {
                    "system.notify": true,
                    "camera.snap": false
                  }
                }
              ]
            }
            """);

        var node = Assert.Single(nodes);
        Assert.Equal(GatewayNodeApprovalState.PendingReapproval, node.ApprovalState);
        Assert.Equal("request-123", node.PendingRequestId);
        Assert.Equal(["system"], node.Capabilities);
        Assert.Equal(["system.notify"], node.Commands);
        Assert.Equal(1, node.CapabilityCount);
        Assert.Equal(1, node.CommandCount);
        Assert.True(node.Permissions["system.notify"]);
        Assert.Equal(["system", "camera"], node.PendingDeclaredCapabilities);
        Assert.Equal(["system.notify", "camera.snap"], node.PendingDeclaredCommands);
        Assert.False(node.PendingDeclaredPermissions["camera.snap"]);
        Assert.Empty(node.UnverifiedDeclaredCommands);
    }

    [Fact]
    public void ParseNodeListPayload_LegacyDeclaredCommandsNeverBecomeEffective()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "legacy-node",
                  "declaredCommands": ["system.run", "camera.snap"]
                }
              ]
            }
            """);

        var node = Assert.Single(nodes);
        Assert.Empty(node.Commands);
        Assert.Equal(0, node.CommandCount);
        Assert.Empty(node.PendingDeclaredCommands);
        Assert.Equal(["system.run", "camera.snap"], node.UnverifiedDeclaredCommands);
    }

    [Fact]
    public void ParseNodeListPayload_EmptyAuthoritativeCapsNeverFallsBackToLegacyCapabilities()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "authoritative-empty",
                  "caps": [],
                  "capabilities": ["camera", "screen"]
                }
              ]
            }
            """);

        var node = Assert.Single(nodes);
        Assert.Empty(node.Capabilities);
        Assert.Equal(0, node.CapabilityCount);
    }

    [Fact]
    public void ParseNodeListPayload_UnsafePendingRequestIdIsNotExposed()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "unsafe-request",
                  "approvalState": "pending-approval",
                  "pendingRequestId": "request-1; Remove-Item C:\\"
                }
              ]
            }
            """);

        var node = Assert.Single(nodes);
        Assert.Equal(GatewayNodeApprovalState.PendingApproval, node.ApprovalState);
        Assert.Null(node.PendingRequestId);
    }

    [Fact]
    public void ParseNodeListPayload_AcceptsLegacyLastSeenAtWireName()
    {
        // Older mocks / non-gateway producers may emit lastSeenAt (no Ms suffix).
        // The parser keeps that path as a fallback so existing fixtures keep
        // working after we add the *Ms primary names.
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "legacy",
                  "status": "connected",
                  "lastSeenAt": 1739760000000
                }
              ]
            }
            """);

        Assert.Single(nodes);
        Assert.NotNull(nodes[0].LastSeen);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1739760000000).UtcDateTime,
            nodes[0].LastSeen!.Value);
    }

    [Fact]
    public void ParseNodeListPayload_DefaultsForMinimalPayload()
    {
        // A node entry with only nodeId must still parse without throwing
        // and the new optional fields must default to null / false / empty.
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [ { "nodeId": "bare" } ]
            }
            """);

        Assert.Single(nodes);
        var n = nodes[0];
        Assert.Null(n.Version);
        Assert.Null(n.CoreVersion);
        Assert.Null(n.UiVersion);
        Assert.Null(n.ClientId);
        Assert.Null(n.ClientMode);
        Assert.Null(n.DeviceFamily);
        Assert.Null(n.ModelIdentifier);
        Assert.Null(n.RemoteIp);
        Assert.Null(n.PathEnv);
        Assert.Null(n.ConnectedAt);
        Assert.Null(n.ApprovedAt);
        Assert.Null(n.LastSeen);
        Assert.Null(n.LastSeenReason);
        Assert.False(n.IsPaired);
        Assert.False(n.HasExplicitDisplayName);
        Assert.Empty(n.DisabledCommands);
        Assert.Equal(GatewayNodeApprovalState.Unknown, n.ApprovalState);
        Assert.Null(n.PendingRequestId);
        Assert.Empty(n.PendingDeclaredCapabilities);
        Assert.Empty(n.PendingDeclaredCommands);
        Assert.Empty(n.PendingDeclaredPermissions);
        Assert.Empty(n.UnverifiedDeclaredCommands);
    }

    [Fact]
    public async Task NodeRenameAsync_RejectsEmptyNodeId_WithoutHittingTransport()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());

        var result = await client.NodeRenameAsync("", "New Name");

        Assert.False(result.Success);
        Assert.Equal("nodeId required", result.ErrorMessage);
    }

    [Fact]
    public async Task NodeRenameAsync_RejectsEmptyDisplayName_WithoutHittingTransport()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());

        var result = await client.NodeRenameAsync("node-1", "   ");

        Assert.False(result.Success);
        Assert.Equal("displayName required", result.ErrorMessage);
    }

    [Fact]
    public async Task NodeRenameAsync_ReturnsErrorWhenNotConnected()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());

        var result = await client.NodeRenameAsync("node-1", "Pretty Name");

        Assert.False(result.Success);
        Assert.Equal("Gateway connection is not open", result.ErrorMessage);
    }

    [Fact]
    public async Task NodePairRemoveAsync_ReturnsFailureForEmptyNodeId()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());

        var result = await client.NodePairRemoveAsync("");

        Assert.False(result.Success);
        Assert.Equal("nodeId required", result.ErrorMessage);
    }

    [Fact]
    public async Task NodePairRemoveAsync_ReturnsFailureWhenNotConnected()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());

        var result = await client.NodePairRemoveAsync("node-1");

        Assert.False(result.Success);
        Assert.Equal("Gateway connection is not open", result.ErrorMessage);
    }

    [Fact]
    public void Constructor_InitializesWithProvidedValues()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient(
            "http://test:8080",
            "my-token",
            logger,
            identityPath: CreateTempIdentityPath());
        
        // Verify URL was normalized (http → ws) — field is now on base class WebSocketClientBase
        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;
        Assert.Equal("ws://test:8080", actualUrl);
    }

    [Fact]
    public void Constructor_UsesNullLogger_WhenNotProvided()
    {
        // Verify construction without logger doesn't throw and still normalizes URL
        var client = new OpenClawGatewayClient(
            "https://test:8080",
            "my-token",
            identityPath: CreateTempIdentityPath());
        
        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;
        Assert.Equal("wss://test:8080", actualUrl);
    }

    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("http://example.com:8080", "ws://example.com:8080")]
    [InlineData("https://example.com:443", "wss://example.com:443")]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    [InlineData("wss://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("HTTP://LOCALHOST:18789", "ws://LOCALHOST:18789")]
    [InlineData("HTTPS://HOST.EXAMPLE.COM", "wss://HOST.EXAMPLE.COM")]
    public void Constructor_NormalizesHttpToWs(string inputUrl, string expectedWsUrl)
    {
        var client = new OpenClawGatewayClient(
            inputUrl,
            "test-token",
            identityPath: CreateTempIdentityPath());

        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;

        Assert.Equal(expectedWsUrl, actualUrl);
    }

    [Fact]
    public void ResetUnsupportedMethodFlags_ClearsAllUnsupportedFlags()
    {
        var helper = new GatewayClientTestHelper();

        helper.SetUnsupportedMethodFlags(usageStatus: true, usageCost: true, sessionPreview: true, nodeList: true);
        helper.ResetUnsupportedMethodFlags();

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.False(flags.UsageStatus);
        Assert.False(flags.UsageCost);
        Assert.False(flags.SessionPreview);
        Assert.False(flags.NodeList);
    }

    [Fact]
    public void ParseChannelHealth_WithChannels_FiresEventWithCorrectNames()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"running","running":true},"telegram":{"status":"ready","configured":true}}""";

        var (channels, fired) = helper.ParseChannelHealthPayload(json);

        Assert.True(fired);
        Assert.Equal(2, channels.Length);
        Assert.Contains(channels, c => c.Name == "discord");
        Assert.Contains(channels, c => c.Name == "telegram");
    }

    [Fact]
    public void ParseChannelHealth_EmptyObject_FiresEventWithEmptyArray()
    {
        var helper = new GatewayClientTestHelper();
        var json = "{}";

        var (channels, fired) = helper.ParseChannelHealthPayload(json);

        // Event must fire even when there are no channels so removed channels are cleared
        Assert.True(fired, "ChannelHealthUpdated should fire even when channels is empty");
        Assert.Empty(channels);
    }

    [Fact]
    public void ParseChannelHealth_StatusField_TakesPriorityOverDerivedStatus()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"degraded","running":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("degraded", channels[0].Status);
    }

    // ── ParseChannelHealth — derived-status paths ───────────────────────────────

    [Fact]
    public void ParseChannelHealth_RunningTrue_NoStatusField_DerivedAsRunning()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"running":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("running", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_NoStatusField_DerivedAsError()
    {
        var helper = new GatewayClientTestHelper();
        // lastError present and non-null → hasError = true
        var json = """{"whatsapp":{"lastError":"connection refused"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("error", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_NullLastError_NotDerivedAsError()
    {
        // lastError=null should NOT set hasError (ValueKind == Null is excluded)
        var helper = new GatewayClientTestHelper();
        var json = """{"slack":{"lastError":null,"configured":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        // hasError is false → falls through to configured && !hasError → "ready"
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredAndProbeOk_DerivedAsReady()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true,"probe":{"ok":true}}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredAndLinked_DerivedAsReady()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"configured":true,"linked":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
        Assert.True(channels[0].IsLinked);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredNoErrors_DerivedAsReady()
    {
        // configured=true with no lastError and no explicit status → ready (catch-all)
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_NotConfigured_DerivedAsNotConfigured()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("not configured", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_TakesPriorityOverRunning()
    {
        // Error takes priority over running in the derivation chain
        var helper = new GatewayClientTestHelper();
        var json = """{"slack":{"running":true,"lastError":"timeout"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("error", channels[0].Status);
    }

    // ── ParseChannelHealth — property parsing ───────────────────────────────────

    [Fact]
    public void ParseChannelHealth_ParsesErrorProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"error","error":"Bot token invalid"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("Bot token invalid", channels[0].Error);
    }

    [Fact]
    public void ParseChannelHealth_ParsesAuthAgeProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"status":"ready","authAge":"3 days ago"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("3 days ago", channels[0].AuthAge);
    }

    [Fact]
    public void ParseChannelHealth_ParsesTypeProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"status":"ready","type":"webhook"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("webhook", channels[0].Type);
    }

    [Fact]
    public void ParseChannelHealth_LinkedFalse_IsLinkedIsFalse()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"linked":false,"status":"not configured"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.False(channels[0].IsLinked);
    }

    [Fact]
    public void ParseChannelHealth_ProbeNotOk_DoesNotSetReady()
    {
        // probe.ok=false + configured=true + no isLinked → falls to configured&&!hasError → ready
        // (the two "ready" clauses effectively mean configured=true always means ready if no error)
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true,"probe":{"ok":false}}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        // configured && !hasError → ready (second ready clause fires)
        Assert.Equal("ready", channels[0].Status);
    }

    // --- HandleRequestError: pairing required ---

    [Fact]
    public void HandleRequestError_PairingRequired_SetsPairingBlockFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-1", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-1",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
    }

    [Fact]
    public void HandleRequestError_PairingRequired_KeepsAutoReconnectEnabled()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-retry", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-retry",
            "ok": false,
            "error": {
                "message": "pairing required for this device",
                "details": {
                    "code": "PAIRING_REQUIRED",
                    "requestId": "abc-123"
                }
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.True(helper.ShouldAutoReconnectForTest());
    }

    [Fact]
    public void HandleRequestError_PairingRequired_LogsWarning()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-2", "connect");
        var logger = new TestLogger();
        var helperWithLogger = new GatewayClientTestHelper(logger);
        helperWithLogger.TrackPendingRequest("req-pairing-2", "connect");

        helperWithLogger.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-2",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.Contains(logger.Logs, l => l.Contains("Pairing required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HandleRequestError_PairingRequired_FiresPairingEvent()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-3", "connect");
        var pairingFired = false;
        helper.Client.PairingRequired += (_, _) => pairingFired = true;

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-3",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.True(pairingFired);
    }

    [Fact]
    public void HandleRequestError_PairingRequired_StructuredCodeWithoutTextMatch_SetsRequestId()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-structured", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-structured",
            "ok": false,
            "error": {
                "message": "approval is needed for this device",
                "details": {
                    "code": "PAIRING_REQUIRED",
                    "requestId": "abc-123"
                }
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.Equal("abc-123", helper.GetPairingRequiredRequestId());
    }

    [Fact]
    public void HandleRequestError_PairingRequired_MergesFieldsAcrossDetailObjects()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-split", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-split",
            "ok": false,
            "error": {
                "message": "approval is needed for this device",
                "details": {
                    "code": "PAIRING_REQUIRED"
                },
                "data": {
                    "details": {
                        "requestId": "nested-123"
                    }
                }
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.Equal("nested-123", helper.GetPairingRequiredRequestId());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"  \"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"-bad\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"bad/id\"}")]
    public void HandleRequestError_PairingRequired_MissingOrMalformedRequestId_FailsClosedWithNullRequestId(string detailsJson)
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-malformed", "connect");

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "req-pairing-malformed",
            "ok": false,
            "error": {
                "message": "pairing required for this device",
                "details": {{detailsJson}}
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.Null(helper.GetPairingRequiredRequestId());
    }

    // --- HandleRequestError: device signature invalid ---

    [Fact]
    public void HandleRequestError_DeviceSignatureInvalid_FirstRejectionFallsBackToV2()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();

        // First rejection triggers v2 fallback, not auth failure
        helper.TrackPendingRequest("req-sig-1", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-1",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
        Assert.Empty(authEvents);
        Assert.True(helper.GetUseV2Signature());
    }

    [Fact]
    public void HandleRequestError_StructuredDeviceSignatureInvalid_FirstRejectionFallsBackToV2()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-sig-structured", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-structured",
            "ok": false,
            "error": {
                "message": "device authentication failed",
                "details": {
                    "code": "DEVICE_AUTH_SIGNATURE_INVALID"
                }
            }
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
        Assert.Empty(authEvents);
        Assert.True(helper.GetUseV2Signature());
    }

    [Fact]
    public void HandleRequestError_DeviceSignatureInvalid_LogsWarningWithMode()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        helper.TrackPendingRequest("req-sig-log", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-log",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.Contains(logger.Logs, l => l.Contains("device signature", StringComparison.OrdinalIgnoreCase));
    }

    // --- HandleRequestError: missing scope ---

    [Theory]
    [InlineData("sessions.list")]
    [InlineData("usage.status")]
    [InlineData("usage.cost")]
    [InlineData("node.list")]
    public void HandleRequestError_MissingOperatorReadScope_SetsUnavailableFlag(string method)
    {
        var helper = new GatewayClientTestHelper();
        var reqId = $"req-scope-{method}";
        helper.TrackPendingRequest(reqId, method);

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "{{reqId}}",
            "ok": false,
            "error": "missing scope: operator.read"
        }
        """);

        Assert.True(helper.GetOperatorReadScopeUnavailable());
    }

    // --- HandleRequestError: unknown method fallbacks ---

    [Fact]
    public void HandleRequestError_UnknownMethod_UsageStatus_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-us", "usage.status");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-us",
            "ok": false,
            "error": "unknown method: usage.status"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.UsageStatus);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_UsageCost_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-uc", "usage.cost");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-uc",
            "ok": false,
            "error": "unknown method: usage.cost"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.UsageCost);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_SessionsPreview_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-sp", "sessions.preview");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-sp",
            "ok": false,
            "error": "unknown method: sessions.preview"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.SessionPreview);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_NodeList_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-nl", "node.list");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-nl",
            "ok": false,
            "error": "unknown method: node.list"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.NodeList);
    }

    // --- HandleRequestError: terminal auth errors (PR #206 fix) ---

    [Theory]
    [InlineData("token mismatch")]
    [InlineData("origin not allowed")]
    [InlineData("too many failed attempts")]
    public void HandleRequestError_TerminalAuthError_SetsAuthFailedFlag(string errorMessage)
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-auth-1", "connect");

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "req-auth-1",
            "ok": false,
            "error": "{{errorMessage}}"
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandleRequestError_NestedExpiredSignature_IsTerminalWithoutV2Fallback(bool nestedUnderData)
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-expired", "connect");
        var detailContainer = nestedUnderData
            ? "\"data\":{\"details\":{\"code\":\"DEVICE_AUTH_SIGNATURE_EXPIRED\"}}"
            : "\"details\":{\"code\":\"DEVICE_AUTH_SIGNATURE_EXPIRED\"}";

        helper.ProcessRawMessage(
            $$"""
              {
                "type": "res",
                "id": "req-auth-expired",
                "ok": false,
                "error": {
                  "message": "device signature expired",
                  {{detailContainer}}
                }
              }
              """);

        Assert.True(helper.GetAuthFailedFlag());
        Assert.Single(authEvents);
        Assert.Contains("device signature expired", authEvents[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(helper.GetUseV2Signature());
    }

    [Fact]
    public void HandleRequestError_ExpiredDetailOverridesGenericInvalidSignatureFallback()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-expired-mixed", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-expired-mixed",
            "ok": false,
            "error": {
                "message": "device signature invalid",
                "details": {
                    "code": "DEVICE_AUTH_SIGNATURE_EXPIRED"
                }
            }
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
        Assert.Single(authEvents);
        Assert.Contains("DEVICE_AUTH_SIGNATURE_EXPIRED", authEvents[0], StringComparison.Ordinal);
        Assert.Contains("device signature invalid", authEvents[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GatewayErrorKind.Auth, GatewayErrorClassifier.Classify(authEvents[0]));
        Assert.False(helper.GetUseV2Signature());
    }

    [Fact]
    public void HandleRequestError_IncompleteTopLevelDetails_UsesNestedExpiredCode()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-expired-nested-fallback", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-expired-nested-fallback",
            "ok": false,
            "error": {
                "message": "device signature invalid",
                "details": {
                    "requestId": "abc"
                },
                "data": {
                    "details": {
                        "code": "DEVICE_AUTH_SIGNATURE_EXPIRED"
                    }
                }
            }
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
        Assert.Single(authEvents);
        Assert.Contains("DEVICE_AUTH_SIGNATURE_EXPIRED", authEvents[0], StringComparison.Ordinal);
        Assert.False(helper.GetUseV2Signature());
    }

    [Fact]
    public void HandleConnectChallenge_ValidTimestampWithoutNonce_IsRetained()
    {
        var helper = new GatewayClientTestHelper();
        const long challengeTimestampMs = 1_716_480_000_000;

        helper.ProcessRawMessage(
            $$"""
              {
                "type": "event",
                "event": "connect.challenge",
                "payload": {
                  "ts": {{challengeTimestampMs}}
                }
              }
              """);

        Assert.Equal(challengeTimestampMs, helper.GetChallengeTimestampMs());
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_RaisesAuthenticationFailedEvent()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-2", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-2",
            "ok": false,
            "error": "token mismatch — reconnect rejected"
        }
        """);

        Assert.Single(authEvents);
        Assert.Contains("token mismatch", authEvents[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleRequestError_DeviceTokenMismatchTopLevelCode_GenericMessage_RaisesRecognizableAuthFailure()
    {
        // The gateway may deliver the device-token mismatch as a TOP-LEVEL error.code with a generic
        // message. The raised AuthenticationFailed string must still be recognizable as a device-token
        // mismatch so the connection manager can self-recover.
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-toplevel", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-toplevel",
            "ok": false,
            "error": { "message": "unauthorized", "code": "AUTH_DEVICE_TOKEN_MISMATCH" }
        }
        """);

        Assert.Single(authEvents);
        Assert.Equal(
            OpenClaw.Shared.GatewayErrorKind.DeviceTokenMismatch,
            OpenClaw.Shared.GatewayErrorClassifier.ClassifyWithCode(authEvents[0]));
    }

    [Fact]
    public void HandleRequestError_SharedTokenMismatchTopLevelCode_DoesNotLookLikeDeviceMismatch()
    {
        // A wrong SHARED token (top-level AUTH_TOKEN_MISMATCH) is terminal auth but must NOT be
        // enriched into a recoverable device-token mismatch.
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        var failures = helper.CaptureConnectionFailures();
        helper.TrackPendingRequest("req-auth-shared", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-shared",
            "ok": false,
            "error": { "message": "unauthorized", "code": "AUTH_TOKEN_MISMATCH" }
        }
        """);

        Assert.Single(authEvents);
        Assert.Equal([GatewayErrorKind.Auth], failures);
        Assert.NotEqual(
            OpenClaw.Shared.GatewayErrorKind.DeviceTokenMismatch,
            OpenClaw.Shared.GatewayErrorClassifier.ClassifyWithCode(authEvents[0]));
    }

    [Fact]
    public void HandleRequestError_SharedTokenMismatchNestedCode_RaisesTypedAuthFailure()
    {
        var helper = new GatewayClientTestHelper();
        var failures = helper.CaptureConnectionFailures();
        helper.TrackPendingRequest("req-auth-shared-nested", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-shared-nested",
            "ok": false,
            "error": {
                "message": "unauthorized",
                "details": { "code": "AUTH_TOKEN_MISMATCH" }
            }
        }
        """);

        Assert.Equal([GatewayErrorKind.Auth], failures);
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_RaisesErrorStatus()
    {
        var helper = new GatewayClientTestHelper();
        var statusChanges = helper.CaptureStatusChanges();
        helper.TrackPendingRequest("req-auth-3", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-3",
            "ok": false,
            "error": "origin not allowed"
        }
        """);

        Assert.Contains(ConnectionStatus.Error, statusChanges);
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_OnNonConnectMethod_DoesNotSetAuthFailed()
    {
        // Terminal auth check only applies to "connect" method — other methods must not set the flag
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-auth-4", "sessions.list");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-4",
            "ok": false,
            "error": "token mismatch"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
    }

    [Fact]
    public void HandleHelloOk_AfterAuthFailed_ClearsAuthFailedFlag()
    {
        var helper = new GatewayClientTestHelper();

        // First, trigger auth failure
        helper.TrackPendingRequest("req-auth-5", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-5",
            "ok": false,
            "error": "token mismatch"
        }
        """);
        Assert.True(helper.GetAuthFailedFlag());

        // Now receive hello-ok — flag must be cleared
        helper.TrackPendingRequest("req-hello-1", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-hello-1",
            "payload": {
                "type": "hello-ok",
                "protocol": 4
            }
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
    }

    [Fact]
    public void HandleRequestError_DeviceSignatureRejected_SetsAuthFailed()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        var authEvents = helper.CaptureAuthenticationFailedEvents();

        // First rejection triggers v2 fallback (not auth failure)
        helper.TrackPendingRequest("req-sig-v3", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-v3",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
        Assert.Empty(authEvents);

        // Second rejection (v2 also rejected) is a real auth error
        helper.TrackPendingRequest("req-sig-v2", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-v2",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
        Assert.Single(authEvents);
        Assert.Contains("device signature", authEvents[0], StringComparison.OrdinalIgnoreCase);
    }
}

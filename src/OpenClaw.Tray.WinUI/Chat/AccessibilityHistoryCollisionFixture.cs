using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClawTray.Chat;

internal static class AccessibilityHistoryCollisionFixture
{
    internal const string FixtureName = "history-collision";
    internal const string ThreadId = "accessibility-main";
    private const string SessionId =
        "accessibility-history-collision-session";

    internal static OpenClawChatDataProvider Create(
        string isolatedDataDirectory,
        Action<Action>? post = null)
    {
        ValidateIsolationGate(
            isolatedDataDirectory,
            Environment.GetEnvironmentVariable);
        return CreateCore(isolatedDataDirectory, post).Provider;
    }

    internal static (
        OpenClawChatDataProvider Provider,
        Bridge GatewayBridge) CreateForTesting(
            string isolatedDataDirectory,
            Func<string, string?> environmentLookup)
    {
        ValidateIsolationGate(
            isolatedDataDirectory,
            environmentLookup);
        return CreateCore(isolatedDataDirectory, post: null);
    }

    private static (
        OpenClawChatDataProvider Provider,
        Bridge GatewayBridge) CreateCore(
            string isolatedDataDirectory,
            Action<Action>? post)
    {
        Directory.CreateDirectory(isolatedDataDirectory);
        var toolCachePath = Path.Combine(
            isolatedDataDirectory,
            "history-collision-tool-metadata.json");
        File.WriteAllText(
            toolCachePath,
            JsonSerializer.Serialize(
                new Dictionary<
                    string,
                    List<ChatMetadataStore.CachedToolMeta>>
                {
                    [SessionId] =
                    [
                        new()
                        {
                            Ts = 100,
                            ToolName = "Bash",
                            Label = "Flattened history command",
                            ToolCallId = "unverified-flat-id",
                            RunId = "unverified-flat-run",
                            ToolArgs = new()
                            {
                                ["command"] =
                                    "synthetic flattened id: history-tool-1",
                            },
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                        new()
                        {
                            Ts = 200,
                            ToolName = "Exec",
                            Label = "Structured history command",
                            ToolCallId = "history-tool-0",
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                    ],
                }));

        var bridge = new Bridge();
        var provider = new OpenClawChatDataProvider(
            bridge,
            post,
            toolCachePath,
            Path.Combine(
                isolatedDataDirectory,
                "history-collision-attachment-metadata.json"),
            Path.Combine(
                isolatedDataDirectory,
                "history-collision-last-chat-state.json"));
        provider.LoadHistoryAsync(ThreadId).GetAwaiter().GetResult();
        return (provider, bridge);
    }

    private static void ValidateIsolationGate(
        string isolatedDataDirectory,
        Func<string, string?> environmentLookup)
    {
        var configuredDataDirectory =
            environmentLookup("OPENCLAW_TRAY_DATA_DIR");
        if (!string.Equals(
                environmentLookup("OPENCLAW_ACCESSIBILITY_TEST_CHAT"),
                "1",
                StringComparison.Ordinal) ||
            !string.Equals(
                environmentLookup(
                    "OPENCLAW_ACCESSIBILITY_TEST_CHAT_FIXTURE"),
                FixtureName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(configuredDataDirectory) ||
            !string.Equals(
                Path.GetFullPath(isolatedDataDirectory),
                Path.GetFullPath(configuredDataDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The history collision fixture requires isolated accessibility test data.");
        }
    }

    internal sealed class Bridge : IChatGatewayBridge
    {
        private static readonly SessionInfo[] Sessions =
        [
            new()
            {
                Key = ThreadId,
                IsMain = true,
                DisplayName = "History collision proof",
                Status = "active",
                Model = "test-model",
            },
        ];

        public bool IsConnected => true;
        public ConnectionStatus CurrentStatus => ConnectionStatus.Connected;
        public string MainSessionKey => ThreadId;
        public bool HasHandshakeSnapshot => true;
        public int HistoryRequestCount { get; private set; }

        public SessionInfo[] GetSessionList() => Sessions;
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }

        public Task<ChatHistoryInfo> RequestChatHistoryAsync(
            string? sessionKey)
        {
            HistoryRequestCount++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionId = SessionId,
                SessionKey = ThreadId,
                Messages =
                [
                    new ChatMessageInfo
                    {
                        Role = "assistant",
                        Ts = 200,
                        ToolContent =
                        [
                            new ChatToolContentInfo
                            {
                                Kind = ChatToolContentKind.Call,
                                CallId = "history-tool-0",
                                ToolName = "Exec",
                            },
                        ],
                    },
                    new ChatMessageInfo
                    {
                        Role = "toolresult",
                        Text =
                            "flattened output owned by history-tool-1",
                        Ts = 300,
                    },
                ],
            });
        }

        public Task SendChatMessageAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null) =>
            Task.CompletedTask;

        public Task<ChatSendResult> SendChatMessageForRunAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null,
            string? idempotencyKey = null) =>
            Task.FromResult(new ChatSendResult());

        public Task<CommandCatalog> ListCommandsAsync(
            CommandCatalogQuery? query = null) =>
            Task.FromResult(new CommandCatalog { IsSupported = false });

        public Task PatchSessionModelAsync(
            string sessionKey,
            string model) =>
            Task.CompletedTask;

        public Task ClearSessionModelAsync(string sessionKey) =>
            Task.CompletedTask;

        public Task PatchSessionThinkingLevelAsync(
            string sessionKey,
            string thinkingLevel) =>
            Task.CompletedTask;

        public Task ClearSessionThinkingLevelAsync(string sessionKey) =>
            Task.CompletedTask;

        public Task SendChatAbortAsync(
            string runId,
            string? sessionKey = null) =>
            Task.CompletedTask;

        public Task ResolveExecApprovalAsync(
            string approvalId,
            string decision) =>
            Task.CompletedTask;

        public void Dispose() { }

        public event EventHandler<ConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionInfo[]>? SessionsUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionCommandResult>?
            SessionCommandCompleted
        {
            add { }
            remove { }
        }

        public event EventHandler<ChatMessageInfo>? ChatMessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentEventInfo>? AgentEventReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<ModelsListInfo>? ModelsListUpdated
        {
            add { }
            remove { }
        }
    }
}

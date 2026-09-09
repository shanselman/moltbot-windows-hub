using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineRenderIdentityContractTests
{
    [Fact]
    public void TimelineRows_UseGenerationQualifiedKindedKeys()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.Contains("public static string RowKey(ChatTimelinePresentationContext props", timeline);
        Assert.Contains("props.TimelineGeneration", timeline);
        Assert.Contains("entry.Kind", timeline);
        Assert.Contains("entry.Id", timeline);
        Assert.Contains(".WithKey(row.Key)", timeline);
        Assert.DoesNotContain(".WithKey(entry.Id)", timeline);
    }

    [Fact]
    public void ThinkingIndicator_UsesSyntheticGenerationQualifiedKey()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.Contains("public static string SyntheticRowKey(ChatTimelinePresentationContext props", timeline);
        Assert.Contains("ReactorChatTimeline.SyntheticRowKey(", timeline);
        Assert.Contains("\"__thinking__\"", timeline);
    }

    [Fact]
    public void TimelineGeneration_FlowsFromProviderSnapshotToTimelineProps()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var state = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatConversationState.cs");
        var resetState = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatResetState.cs");
        var projector = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatSnapshotProjector.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("IReadOnlyDictionary<string, long>? TimelineGenerations = null", models);
        Assert.Contains("new Dictionary<string, long>(_versions)", resetState);
        Assert.Contains("_reset.SnapshotVersions()", state);
        Assert.Contains("private readonly ChatConversationState _state", provider);
        Assert.DoesNotContain("private readonly object _gate", provider);
        Assert.Contains("TimelineGenerations: input.TimelineGenerations", projector);
        Assert.Contains("snapshot.TimelineGenerations", root);
        Assert.Contains("var timelineProps = new ChatTimelinePresentationContext(", root);
        Assert.Contains("timelineGeneration,", root);
    }

    [Fact]
    public void QueuedMessages_RenderInComposerAboveInput()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var state = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatConversationState.cs");
        var queueState = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatQueueState.cs");
        var projector = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatSnapshotProjector.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatComposer.cs");

        Assert.Contains("public record ChatQueuedMessage", models);
        Assert.Contains("QueuedMessagesByThread", models);
        Assert.Contains("Dictionary<string, List<ChatQueuedMessage>> _messages", queueState);
        Assert.Contains("_messages.ToDictionary(", queueState);
        Assert.Contains("_queue.SnapshotMessages()", state);
        Assert.DoesNotContain("Dictionary<string, List<ChatQueuedMessage>> _queuedMessages", provider);
        Assert.Contains("QueuedMessagesByThread: input.QueuedMessages", projector);
        Assert.Contains("snapshot.QueuedMessagesByThread", root);
        Assert.Contains("QueuedMessages: queuedMessages", root);
        Assert.Contains("var queuedRows = inputs.QueuedMessages", composer);
        Assert.Contains("Element queuedPanel = queuedRows.Length == 0", composer);
        Assert.Contains("ScrollView(VStack(4, queuedRows))", composer);
        Assert.Contains("Chat_Composer_QueuedMessageCancel", composer);
        Assert.Contains("Chat_Composer_QueuedMessageCancelAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailed", composer);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailedAutomationFormat", composer);
        Assert.Contains("ChatQueuedMessageRemoveFailed", composer);
        Assert.Contains("ChatQueuedMessageCancel", composer);
        Assert.Contains("Chat_Composer_QueuedCountFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageFailedAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageFailed", composer);
        Assert.Contains("ChatQueuedMessageSendState.Sending", composer);
    }

    [Fact]
    public void Composer_DisablesMessageOptionDropdownsWhileTurnOrPendingQueueSendIsActive()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatComposer.cs");

        Assert.Contains("timeline.TurnActive || hasPendingQueuedSend", root);
        Assert.Contains("message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending", root);
        Assert.Contains(".IsEnabled(enabled)", composer);
        Assert.Equal(3, Regex.Matches(composer, @"!inputs\.MessageOptionsDisabled").Count);
    }

    [Fact]
    public void Composer_PreservesInputAndAttachmentsWhenSendThrows()
    {
        var controller = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatComposerController.cs");

        Assert.Contains("var accepted = await SendCoreAsync(", controller);
        Assert.Contains("if (accepted)", controller);
        Assert.Contains("_vm.RemoveSubmittedAttachments(attachments);", controller);
        Assert.Matches(
            new Regex(
                @"catch \(Exception ex\)\s*\{\s*System\.Diagnostics\.Trace\.WriteLine\(\$""\[chat\] composer send failed: \{ex\}""\);\s*return false;\s*\}",
                RegexOptions.Multiline),
            controller);
    }

    [Fact]
    public void Timeline_DoesNotRenderTemporaryDebugMetadata()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.DoesNotContain("BuildDebugMetadata", timeline);
        Assert.DoesNotContain("DEBUG kind=", timeline);
        Assert.DoesNotContain("rowGen=", timeline);
        Assert.DoesNotContain("localQueued=", timeline);
        Assert.DoesNotContain("textHash=", timeline);
    }

    [Fact]
    public void ResetClearPath_BumpsTimelineGenerationBeforeReusingEntryIds()
    {
        var state = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatConversationState.cs");
        var resetState = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatResetState.cs");

        Assert.Matches(
            new Regex(@"internal\s+ChatResetTransition\s+ResetThread\([\s\S]*lock\s*\(_gate\)[\s\S]*_reset\.BeginReset\([\s\S]*_timelines\[threadId\]\s*=\s*ChatTimelineState\.Initial\(\)\s*with\s*\{\s*HistoryLoaded\s*=\s*true"),
            state);
        Assert.Matches(
            new Regex(@"internal\s+long\s+BeginReset\([\s\S]*_versions\[threadId\]\s*=\s*generation;"),
            resetState);
    }

    [Fact]
    public void ReactorToolRows_RenderSafeArgsAndLocalizedStatusWithoutChangingRowKeys()
    {
        var renderer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ToolCallCardRenderer.cs");

        Assert.Contains("FormatToolDisplayArgs(entry.ToolArgs)", renderer);
        Assert.Contains("foreach (var key in NativeToolProjector.DisplayArgumentKeys)", renderer);
        Assert.DoesNotContain(
            "new[] { \"command\", \"path\", \"file_path\", \"query\", \"url\", \"pattern\" }",
            renderer);
        Assert.Contains("Chat_Tool_InputSection", renderer);
        Assert.Contains("Chat_Status_Running", renderer);
        Assert.Contains("Chat_Status_Done", renderer);
        Assert.Contains("Chat_Status_Error", renderer);
        Assert.Contains("Chat_Status_Interrupted", renderer);
        Assert.Contains("Chat_Tool_CallLabel", renderer);
        Assert.Contains("tool-expander:{entry.Id}:collapse:{props.ToolCallsCollapseVersion}", renderer);
        Assert.DoesNotContain("entry.ToolArgs.ToJsonString", renderer);
        Assert.DoesNotContain("{entry.ToolResult}", renderer);
        Assert.DoesNotContain("ToolRunId", renderer);
        Assert.DoesNotContain("ToolLegacyTurn", renderer);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}

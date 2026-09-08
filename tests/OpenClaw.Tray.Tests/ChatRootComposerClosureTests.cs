using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Narrow source-shape guard for the D2 <c>reactor-chat-root-composer-closed</c>
/// ledger row. <see cref="OpenClawTray.Chat.OpenClawReactorChatRoot"/> must not
/// regain composer draft/attachment/slash/voice/send mutable state or direct
/// composer provider workflow calls; that ownership now lives in
/// <see cref="OpenClawTray.Chat.ChatComposerViewModel"/> and
/// <see cref="OpenClawTray.Chat.ChatComposerController"/>. See
/// docs/ARCHITECTURE.md for the retirement condition.
/// </summary>
public sealed class ChatRootComposerClosureTests
{
    [Fact]
    public void Root_DoesNotReintroduceComposerMutableState()
    {
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));

        // Composer draft/attachment/slash/voice/send state must not come back as
        // Reactor UseState/refs on the root.
        Assert.DoesNotContain("pendingAttachments", root);
        Assert.DoesNotContain("speakerMuted", root);
        Assert.DoesNotContain("voiceTranscript", root);
        Assert.DoesNotContain("voiceAudioLevel", root);
        Assert.DoesNotContain("slashMenuState", root);
        Assert.DoesNotContain("ReactorSlashMenuState", root);
        Assert.DoesNotContain("sendInFlight", root);
        Assert.DoesNotContain("voiceCancellation", root);
        Assert.DoesNotContain("voiceOperation", root);

        // The root must not call composer send/model/thinking/catalog provider
        // APIs directly; those now go through ChatComposerController.
        Assert.DoesNotContain("props.Provider.SendMessageAsync", root);
        Assert.DoesNotContain("props.Provider.SetModelAsync", root);
        Assert.DoesNotContain("props.Provider.ClearModelAsync", root);
        Assert.DoesNotContain("props.Provider.SetThinkingLevelAsync", root);
        Assert.DoesNotContain("props.Provider.EnsureCommandCatalogAsync", root);
        Assert.DoesNotContain("props.Provider.CancelQueuedMessageAsync", root);
        Assert.DoesNotContain("ChatLifecycleCommandParser", root);

        // Allowed residue: the root still owns provider subscription, selection,
        // timeline projection, and constructs the composer's immutable inputs.
        Assert.Contains("nativeProvider.LoadHistoryAsync", root);
        Assert.Contains("var composerInputs = new ChatComposerInputs(", root);
        Assert.DoesNotContain("ComposerSession.ApplyInputs", root);
        Assert.Contains("props.ComposerSession.Controller.BindSelectionHandoff(SelectThread)", root);

        // Applying those inputs is a post-commit view effect keyed by stable source
        // values. It must never move back into the root's Render path.
        Assert.Contains("props.Session.ApplyInputs(inputs);", composer);
        Assert.Contains("}), props.InputSnapshot, inputs.CurrentThread);", composer);
        Assert.DoesNotContain("if (vm.Inputs is not { } inputs)", composer);
    }
}

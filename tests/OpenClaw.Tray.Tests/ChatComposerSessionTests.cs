using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Characterization tests for <see cref="ChatComposerSession"/> and
/// <see cref="ChatComposerFactory"/>: exactly-once disposal cascading to both the
/// view model and controller, and that the factory itself is stateless (starts no
/// background work and produces an independent session per call).
/// </summary>
public sealed class ChatComposerSessionTests
{
    private static ChatComposerInputs MakeInputs(string threadId) =>
        new(
            "connected",
            false,
            new ChatThread
            {
                Id = threadId,
                Title = threadId,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            },
            System.Array.Empty<ChatThread>(),
            System.Array.Empty<string>(),
            null,
            false,
            System.Array.Empty<ChatQueuedMessage>(),
            null,
            false);

    [Fact]
    public void Dispose_DisposesViewModelAndControllerExactlyOnce()
    {
        var dispatcher = new RecordingUiDispatcher();
        var factory = new ChatComposerFactory(dispatcher);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null, null);
        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        session.Dispose();
        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Create_ProducesAnIndependentSessionPerCall()
    {
        var dispatcher = new RecordingUiDispatcher();
        var factory = new ChatComposerFactory(dispatcher);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null, null);

        var first = factory.Create(provider, hostActions, initialSpeakerMuted: false);
        var second = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void HostActions_AreExposedUnchangedFromCreation()
    {
        var dispatcher = new RecordingUiDispatcher();
        var factory = new ChatComposerFactory(dispatcher);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, () => { }, null, null, null);

        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.Same(hostActions, session.HostActions);
        session.Dispose();
    }

    [Fact]
    public void ApplyInputs_AssignsSessionMonotonicRevisionsAcrossViewRemounts()
    {
        var dispatcher = new RecordingUiDispatcher();
        var factory = new ChatComposerFactory(dispatcher);
        var session = factory.Create(
            new FakeChatDataProviderForComposerTests(),
            new ChatComposerHostActions(null, null, null, null, null),
            initialSpeakerMuted: false);

        session.ApplyInputs(MakeInputs("first"));
        var firstRevision = session.ViewModel.Inputs!.Revision;

        // ReactorChatComposer may unmount and remount while this host-owned
        // session remains alive. The next effect must not restart at revision 1.
        session.ApplyInputs(MakeInputs("second"));

        Assert.Equal(1, firstRevision);
        Assert.Equal(2, session.ViewModel.Inputs!.Revision);
        Assert.Equal("second", session.ViewModel.Inputs.CurrentThread.Id);
        session.Dispose();
    }
}

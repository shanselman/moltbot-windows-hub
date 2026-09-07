using OpenClaw.Chat;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Chat;

/// <summary>
/// Immutable per-render projection that <see cref="OpenClawReactorChatRoot"/> passes
/// to <see cref="ReactorChatComposer"/> after resolving the provider snapshot,
/// selection, and effective thread. The view applies it to the composer session
/// after the render commits. This is render-only truth:
/// <see cref="ChatComposerViewModel"/> never mutates it and never subscribes to the
/// provider directly.
/// </summary>
/// <remarks>
/// <see cref="Revision"/> is assigned by <see cref="ChatComposerSession"/> only when
/// the Reactor effect observes a changed input source. <see cref="ChatComposerViewModel.ApplyInputs"/>
/// rejects stale revisions and semantically unchanged projections, so an out-of-order
/// dispatch cannot regress state and an accidental repeated effect cannot create a
/// render-notification feedback loop.
/// </remarks>
internal sealed record ChatComposerInputs(
    string ConnectionState,
    bool TurnActive,
    ChatThread CurrentThread,
    IReadOnlyList<ChatThread> AvailableChannels,
    string[] AvailableModels,
    IReadOnlyList<ChatModelChoice>? ModelChoices,
    bool MessageOptionsDisabled,
    IReadOnlyList<ChatQueuedMessage> QueuedMessages,
    IReadOnlyList<GatewayCommand>? AvailableCommands,
    bool CommandsSupported)
{
    internal long Revision { get; init; }

    internal bool HasSameProjection(ChatComposerInputs other) =>
        string.Equals(ConnectionState, other.ConnectionState, StringComparison.Ordinal)
        && TurnActive == other.TurnActive
        && Equals(CurrentThread, other.CurrentThread)
        && AvailableChannels.SequenceEqual(other.AvailableChannels)
        && AvailableModels.SequenceEqual(other.AvailableModels, StringComparer.Ordinal)
        && SequenceEqual(ModelChoices, other.ModelChoices)
        && MessageOptionsDisabled == other.MessageOptionsDisabled
        && QueuedMessages.SequenceEqual(other.QueuedMessages)
        && SequenceEqual(AvailableCommands, other.AvailableCommands)
        && CommandsSupported == other.CommandsSupported;

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return left.SequenceEqual(right);
    }
}

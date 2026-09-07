using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace OpenClawTray.Chat;

/// <summary>
/// WinUI/XAML-free, disposable view model that owns only the composer's ephemeral,
/// host-local observable state: draft text and revision, pending attachment
/// identities, send/voice busy flags, slash UI state (via the existing pure
/// <see cref="ReactorSlashCommandController"/>), transient dismissal/catalog-awaiting
/// presentation, and the latest immutable <see cref="ChatComposerInputs"/> projection.
/// </summary>
/// <remarks>
/// This type never subscribes to <see cref="IChatDataProvider"/> and never owns a
/// file, manager, cache, or runtime generation. Every mutation is dispatched through
/// <see cref="IUiDispatcher"/> so <see cref="PropertyChanged"/> is always raised on the
/// UI thread, even when a background completion (voice transcript, send result) drives
/// the mutation. <see cref="Inputs"/> is render-only truth: <see cref="ApplyInputs"/>
/// rejects any projection whose <see cref="ChatComposerInputs.Revision"/> is not
/// greater than the currently-applied one.
/// </remarks>
internal sealed class ChatComposerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly object _queueLock = new();

    /// <summary>Linearizes "apply one dequeued mutation and raise PropertyChanged"
    /// against <see cref="Dispose"/>. Held only around the apply/notify step, never
    /// across the dequeue (so <see cref="_queueLock"/> and this lock are never
    /// nested from the drain side) and never across the whole drain loop (so a
    /// reentrant <see cref="PropertyChanged"/> subscriber that enqueues another
    /// mutation cannot deadlock: <see cref="Mutate"/> only ever needs
    /// <see cref="_queueLock"/>). <see cref="Dispose"/> acquires this lock before
    /// marking the view model disposed, so an apply that has already started is
    /// guaranteed to finish (and raise its notification) before <see cref="Dispose"/>
    /// can return, while an apply that has not yet started observes disposal and
    /// drops its mutation with no state, revision, or notification change.</summary>
    private readonly object _applyLock = new();

    private readonly Queue<Func<bool>> _pendingMutations = new();
    private bool _draining;

    /// <summary>Disposal flag read under three different synchronization domains:
    /// with <see cref="_queueLock"/> held (<see cref="Mutate"/>'s authoritative
    /// check, <see cref="DrainQueue"/>'s empty-queue check), with
    /// <see cref="_applyLock"/> held (<see cref="DrainQueue"/>'s linearization
    /// recheck before applying), and with neither lock held at all
    /// (<see cref="Mutate"/>'s unlocked fast-path check, <see cref="IsDisposed"/>).
    /// A plain <see cref="bool"/> write under one lock is not guaranteed visible to
    /// a reader synchronized on a different lock or on no lock — a monitor's
    /// acquire/release barrier only orders operations for threads that enter that
    /// same monitor. <see langword="volatile"/> gives every read/write acquire/
    /// release semantics regardless of which (if any) lock is held, so the flip in
    /// <see cref="Dispose"/> is guaranteed visible to every reader above without
    /// widening or nesting the existing two-lock design.</summary>
    private volatile bool _disposed;

    /// <summary>Test-only synchronization seam invoked immediately after a mutation
    /// is dequeued and before <see cref="_applyLock"/> is acquired to apply it.
    /// Always <see langword="null"/> (a no-op) in production; exists solely so a
    /// test can deterministically land inside the linearization gap between
    /// "dequeued" and "applied-or-dropped" and prove which outcome wins against a
    /// concurrent <see cref="Dispose"/>, without relying on unpredictable OS thread
    /// scheduling. Assigned only from <c>OpenClaw.Tray.Tests</c>, which links this
    /// file directly into its own compilation via a <c>&lt;Compile Include&gt;</c>
    /// source link rather than referencing the built WinUI assembly — so the WinUI
    /// project's own compilation never sees an assignment, hence the explicit
    /// suppression below.</summary>
#pragma warning disable CS0649 // Assigned only by a source-linked test compile item, not by this project.
    internal Action? TestOnlyAfterDequeueBeforeApplyLock;
#pragma warning restore CS0649

    private string _draft = string.Empty;
    private long _draftRevision;
    private IReadOnlyList<ChatAttachment> _pendingAttachments = Array.Empty<ChatAttachment>();
    private bool _isSending;
    private bool _isRecording;
    private bool _isSpeakerMuted;
    private string? _voiceTranscript;
    private float _voiceAudioLevel;
    private ReactorSlashMenuState _slashMenuState = ReactorSlashMenuState.Closed;
    private int? _dismissedSlashInputRevision;
    private bool _awaitingCatalog;
    private ChatComposerInputs? _inputs;
    private long _latestInputsRevision;

    public ChatComposerViewModel(IUiDispatcher dispatcher, bool initialSpeakerMuted)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isSpeakerMuted = initialSpeakerMuted;
        SlashDisplay = ReactorSlashDisplayState.Inactive;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Bumped on every accepted mutation. The Reactor view uses this as its
    /// single re-render invalidation token; it is an adapter detail, not a second
    /// copy of composer state.</summary>
    public int RenderRevision { get; private set; }

    public string Draft => _draft;
    public long DraftRevision => _draftRevision;
    public IReadOnlyList<ChatAttachment> PendingAttachments => _pendingAttachments;
    public bool IsSending => _isSending;
    public bool IsRecording => _isRecording;
    public bool IsSpeakerMuted => _isSpeakerMuted;
    public string? VoiceTranscript => _voiceTranscript;
    public float VoiceAudioLevel => _voiceAudioLevel;
    public ReactorSlashMenuState SlashMenuState => _slashMenuState;
    public ReactorSlashDisplayState SlashDisplay { get; private set; }
    public ChatComposerInputs? Inputs => _inputs;

    /// <summary>Exposed for disposal characterization tests.</summary>
    internal bool IsDisposed => _disposed;


    public bool CanSend =>
        _inputs is { } inputs
        && inputs.ConnectionState == "connected"
        && !_isSending
        && !SlashDisplay.IsLoading
        && (_draft.Trim().Length > 0 || _pendingAttachments.Count > 0);

    /// <summary>Applies the latest immutable projection from the root. Rejects any
    /// projection whose revision does not strictly advance the applied one, so an
    /// out-of-order dispatcher application cannot regress session/model/thinking/
    /// queue/connection state.</summary>
    public void ApplyInputs(ChatComposerInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        MutateIfChanged(() =>
        {
            if (inputs.Revision <= _latestInputsRevision)
                return false;
            _latestInputsRevision = inputs.Revision;
            if (_inputs is { } applied && applied.HasSameProjection(inputs))
                return false;

            _inputs = inputs;
            RecomputeSlashDisplay();
            return true;
        });
    }

    public void SetDraft(string value)
    {
        Mutate(() =>
        {
            _draftRevision++;
            _dismissedSlashInputRevision = null;
            _draft = value;
            _slashMenuState = ReactorSlashCommandController.ReconcileState(
                _draft,
                _inputs?.AvailableCommands,
                _slashMenuState);
            RecomputeSlashDisplay();
        });
    }

    public void ClearDraft()
    {
        Mutate(() =>
        {
            _draftRevision++;
            _draft = string.Empty;
            _slashMenuState = ReactorSlashCommandController.ReconcileState(
                _draft,
                _inputs?.AvailableCommands,
                _slashMenuState);
            RecomputeSlashDisplay();
        });
    }

    public void AppendVoiceTranscript(string transcript)
    {
        var draft = _draft.TrimEnd();
        SetDraft(draft.Length == 0 ? transcript : $"{draft} {transcript}");
    }

    public void CommitSlashText(string value, ReactorSlashMenuState nextState)
    {
        Mutate(() =>
        {
            _draftRevision++;
            _draft = value;
            _slashMenuState = nextState;
            RecomputeSlashDisplay();
        });
    }

    public void MoveSlashSelection(int delta)
    {
        Mutate(() =>
        {
            _slashMenuState = ReactorSlashCommandController.MoveSelection(_slashMenuState, SlashDisplay, delta);
            RecomputeSlashDisplay();
        });
    }

    public void DismissSlashMenu()
    {
        Mutate(() =>
        {
            _dismissedSlashInputRevision = (int)_draftRevision;
            _slashMenuState = ReactorSlashMenuState.Closed;
            RecomputeSlashDisplay();
        });
    }

    /// <summary>Commits the currently-selected slash item, if any. Mirrors the
    /// pre-D2 <c>CommitSlashText</c> call after <c>ReactorSlashCommandController.CommitSelection</c>.</summary>
    public ReactorSlashCommitResult CommitSelectedSlashItem()
    {
        var commit = ReactorSlashCommandController.CommitSelection(SlashDisplay);
        if (commit.Accepted)
            CommitSlashText(commit.Text, commit.NextState);
        return commit;
    }

    /// <summary>Returns true exactly once per catalog-awaiting transition, matching
    /// the pre-D2 <c>awaitingCatalog</c> ref semantics.</summary>
    public bool ShouldRequestCatalogOnOpen()
    {
        var shouldRequest = ReactorSlashCommandController.ShouldRequestCatalogOnOpen(_awaitingCatalog, SlashDisplay);
        _awaitingCatalog = SlashDisplay.ShouldRequestCatalog;
        return shouldRequest;
    }

    public void ReconcileAfterCatalogRefresh()
    {
        if (!ReactorSlashCommandController.ShouldReconcileAfterCatalogRefresh(
                (int)_draftRevision,
                _dismissedSlashInputRevision))
        {
            return;
        }

        Mutate(() =>
        {
            _slashMenuState = ReactorSlashCommandController.ReconcileState(
                _draft,
                _inputs?.AvailableCommands,
                _slashMenuState);
            RecomputeSlashDisplay();
        });
    }

    public void AddAttachments(IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
            return;

        Mutate(() => _pendingAttachments = _pendingAttachments.Concat(attachments).ToArray());
    }

    public void RemoveAttachment(ChatAttachment attachment)
    {
        Mutate(() =>
        {
            var next = new List<ChatAttachment>(_pendingAttachments.Count);
            var removed = false;
            foreach (var current in _pendingAttachments)
            {
                if (!removed && ReferenceEquals(current, attachment))
                {
                    removed = true;
                    continue;
                }

                next.Add(current);
            }

            if (removed)
                _pendingAttachments = next;
        });
    }

    /// <summary>Removes only the attachments an accepted send actually submitted, by
    /// reference identity, so attachments added while the send was in flight survive.</summary>
    public void RemoveSubmittedAttachments(IReadOnlyList<ChatAttachment> submitted) =>
        Mutate(() => _pendingAttachments = ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
            _pendingAttachments,
            submitted));

    public void SetSending(bool value) => Mutate(() => _isSending = value);

    public void SetRecording(bool value) => Mutate(() =>
    {
        _isRecording = value;
        RecomputeSlashDisplay();
    });

    public void SetVoiceTranscript(string? value) => Mutate(() => _voiceTranscript = value);

    public void SetVoiceAudioLevel(float value) => Mutate(() => _voiceAudioLevel = value);

    public void SetSpeakerMuted(bool value) => Mutate(() => _isSpeakerMuted = value);

    private void RecomputeSlashDisplay()
    {
        var commandModeEnabled = _inputs?.ConnectionState == "connected" && !_isRecording;
        SlashDisplay = ReactorSlashCommandController.Evaluate(
            _draft,
            _slashMenuState,
            commandModeEnabled,
            _inputs?.CommandsSupported ?? false,
            _inputs?.AvailableCommands);
    }

    /// <summary>Enqueues <paramref name="change"/> onto a single internal FIFO and
    /// ensures exactly one drain is scheduled/running. This — not a per-call
    /// <c>TryEnqueue</c> fast path — is what guarantees queued provider/host inputs,
    /// completions, and user edits apply in the order they were enqueued: a mutation
    /// that arrives while on the UI thread still drains any already-queued
    /// background-originated work first, before (and in the same drain pass as) its
    /// own change. Rejected (no-op) after <see cref="Dispose"/> so a late background
    /// completion cannot mutate a disposed view model or notify a detached view.</summary>
    private void Mutate(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        EnqueueMutation(() =>
        {
            change();
            return true;
        });
    }

    private void MutateIfChanged(Func<bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        EnqueueMutation(change);
    }

    private void EnqueueMutation(Func<bool> change)
    {
        if (_disposed)
            return;

        bool shouldScheduleDrain;
        lock (_queueLock)
        {
            if (_disposed)
                return;

            _pendingMutations.Enqueue(change);
            shouldScheduleDrain = !_draining;
            if (shouldScheduleDrain)
                _draining = true;
        }

        if (!shouldScheduleDrain)
            return;

        if (_dispatcher.HasThreadAccess)
        {
            DrainQueue();
            return;
        }

        if (_dispatcher.TryEnqueue(DrainQueue))
            return;

        // The dispatcher refused the drain (for example, it is shutting down).
        // Drop the queued work safely rather than leaving it stuck forever or
        // applying it out of order later, and diagnose per the repo's existing
        // "dispatcher rejected the work item" convention (see
        // ReactorChatHostExtensions.AsPost) instead of failing silently.
        lock (_queueLock)
        {
            _pendingMutations.Clear();
            _draining = false;
        }

        System.Diagnostics.Debug.WriteLine(
            "Dropped chat composer UI update because DispatcherQueue rejected the drain.");
    }

    /// <summary>Drains the FIFO to empty, applying each queued mutation and raising
    /// exactly one <see cref="PropertyChanged"/> per applied item, always on the UI
    /// thread. Runs until the queue is observed empty under the lock, so mutations
    /// enqueued while a drain is already in progress are picked up by that same
    /// drain rather than needing a second scheduled pass.</summary>
    /// <remarks>
    /// Dequeues under <see cref="_queueLock"/> only, then releases it before
    /// acquiring <see cref="_applyLock"/> to apply — so <see cref="_queueLock"/> is
    /// never held across the mutation action or the <see cref="PropertyChanged"/>
    /// callback. <see cref="_applyLock"/> is rechecked for disposal immediately
    /// after acquiring it: this is the linearization point. If <see cref="Dispose"/>
    /// wins the race (acquires <see cref="_applyLock"/> first and marks the view
    /// model disposed), the dequeued item is dropped here with no state, revision,
    /// or notification change. If this drain wins (acquires <see cref="_applyLock"/>
    /// first), the apply and notification are guaranteed to complete — and any
    /// concurrent <see cref="Dispose"/> call is guaranteed to block until they do —
    /// before <see cref="Dispose"/> can return.
    /// </remarks>
    private void DrainQueue()
    {
        while (true)
        {
            Func<bool> next;
            lock (_queueLock)
            {
                if (_disposed)
                {
                    _pendingMutations.Clear();
                    _draining = false;
                    return;
                }

                if (_pendingMutations.Count == 0)
                {
                    _draining = false;
                    return;
                }

                next = _pendingMutations.Dequeue();
            }

            TestOnlyAfterDequeueBeforeApplyLock?.Invoke();

            lock (_applyLock)
            {
                // Linearization point: if Dispose already ran (or is running and
                // got here first), drop this item outright — no state mutation, no
                // revision bump, no notification. A reentrant PropertyChanged
                // subscriber that calls Dispose() synchronously from inside this
                // same apply is fine too: Monitor locks are reentrant for the
                // owning thread, so Dispose's own lock(_applyLock) block for the
                // disposed-flag flip still runs (and clears the queue) before this
                // apply's try block below observes _disposed and can act on it for
                // any FUTURE loop iteration; this iteration's own apply, having
                // already begun, still completes below exactly once.
                if (_disposed)
                    continue;

                try
                {
                    if (next())
                    {
                        RenderRevision++;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                    }
                }
                catch (Exception ex)
                {
                    // A single misbehaving mutation action or PropertyChanged
                    // subscriber must never wedge _draining=true forever (which
                    // would silently and permanently freeze every future composer
                    // UI update) or strand the remaining queued work behind it. Log
                    // and keep draining the rest of the queue, matching the "no
                    // silent failure" handling already used for a rejected drain
                    // (in Mutate) and for FireAndForget operations in
                    // ChatComposerController.
                    System.Diagnostics.Debug.WriteLine($"Chat composer UI update mutation failed: {ex}");
                }
            }
        }
    }

    /// <summary>Marks the view model disposed and stops all future notification.
    /// Idempotent: repeated calls are a no-op. Acquires <see cref="_applyLock"/>
    /// first (blocking until any apply currently in flight — one that had already
    /// started before this call — completes and raises its notification), then
    /// separately clears any still-pending queued work under
    /// <see cref="_queueLock"/>. The two lock acquisitions are sequential, never
    /// nested, so there is no lock-order cycle with <see cref="DrainQueue"/> (which
    /// only ever holds one of the two locks at a time).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_applyLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            PropertyChanged = null;
        }

        lock (_queueLock)
        {
            _pendingMutations.Clear();
            _draining = false;
        }
    }
}

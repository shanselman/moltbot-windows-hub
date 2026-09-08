using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Characterization tests for <see cref="ChatComposerViewModel"/>: draft/attachment
/// ownership, the <see cref="ChatComposerInputs"/> monotonic-revision guard, derived
/// <see cref="ChatComposerViewModel.CanSend"/>, UI-thread dispatch of every mutation,
/// and exactly-once disposal with no late notification.
/// </summary>
public sealed class ChatComposerViewModelTests
{
    private static ChatThread MakeThread(string id = "session-1", string? model = null, string? thinking = null) =>
        new()
        {
            Id = id,
            Title = "Test Session",
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
            Model = model,
            ThinkingLevel = thinking,
        };

    private static ChatComposerInputs MakeInputs(
        long revision = 1,
        string connectionState = "connected",
        bool turnActive = false,
        ChatThread? thread = null) =>
        new(
            connectionState,
            turnActive,
            thread ?? MakeThread(),
            System.Array.Empty<ChatThread>(),
            System.Array.Empty<string>(),
            null,
            false,
            System.Array.Empty<ChatQueuedMessage>(),
            null,
            false)
        {
            Revision = revision,
        };

    [Fact]
    public void SetDraft_UpdatesDraftAndBumpsRevision()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);

        vm.SetDraft("hello");

        Assert.Equal("hello", vm.Draft);
        Assert.Equal(1, vm.DraftRevision);
    }

    [Fact]
    public void ClearDraft_ResetsDraftAndStillBumpsRevision()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.SetDraft("hello");

        vm.ClearDraft();

        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(2, vm.DraftRevision);
    }

    [Fact]
    public void RemoveAttachment_UsesReferenceEqualityNotValueEquality()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var original = new ChatAttachment { FileName = "file.txt" };
        var duplicate = new ChatAttachment { FileName = "file.txt" };
        vm.AddAttachments(new[] { original, duplicate });

        vm.RemoveAttachment(original);

        Assert.Single(vm.PendingAttachments);
        Assert.Same(duplicate, vm.PendingAttachments[0]);
    }

    [Fact]
    public void RemoveSubmittedAttachments_PreservesAttachmentsAddedWhileSending()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var submitted = new ChatAttachment { FileName = "submitted.txt" };
        vm.AddAttachments(new[] { submitted });
        vm.AddAttachments(new[] { new ChatAttachment { FileName = "added-later.txt" } });

        vm.RemoveSubmittedAttachments(new[] { submitted });

        Assert.Single(vm.PendingAttachments);
        Assert.Equal("added-later.txt", vm.PendingAttachments[0].FileName);
    }

    [Fact]
    public void ApplyInputs_RejectsOutOfOrderRevision()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var newer = MakeInputs(revision: 5, thread: MakeThread("newer"));
        var stale = MakeInputs(revision: 3, thread: MakeThread("stale"));
        var propertyChangedCount = 0;
        vm.PropertyChanged += (_, _) => propertyChangedCount++;

        vm.ApplyInputs(newer);
        var renderRevision = vm.RenderRevision;
        vm.ApplyInputs(stale);

        Assert.Equal("newer", vm.Inputs!.CurrentThread.Id);
        Assert.Equal(renderRevision, vm.RenderRevision);
        Assert.Equal(1, propertyChangedCount);
    }

    [Fact]
    public void ApplyInputs_SemanticallyEquivalentProjection_DoesNotNotify()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var propertyChangedCount = 0;
        vm.PropertyChanged += (_, _) => propertyChangedCount++;

        vm.ApplyInputs(MakeInputs(revision: 1, thread: MakeThread("same")));
        var renderRevision = vm.RenderRevision;
        vm.ApplyInputs(MakeInputs(revision: 2, thread: MakeThread("same")));

        Assert.Equal(1, propertyChangedCount);
        Assert.Equal(renderRevision, vm.RenderRevision);
        Assert.Equal(1, vm.Inputs!.Revision);
    }

    [Fact]
    public void ApplyInputs_NewerEquivalentProjection_AdvancesStaleWatermark()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var propertyChangedCount = 0;
        vm.PropertyChanged += (_, _) => propertyChangedCount++;

        vm.ApplyInputs(MakeInputs(revision: 1, thread: MakeThread("current")));
        vm.ApplyInputs(MakeInputs(revision: 3, thread: MakeThread("current")));
        vm.ApplyInputs(MakeInputs(revision: 2, thread: MakeThread("stale")));

        Assert.Equal("current", vm.Inputs!.CurrentThread.Id);
        Assert.Equal(1, vm.RenderRevision);
        Assert.Equal(1, propertyChangedCount);
    }

    [Fact]
    public void ChatComposerInputs_ProjectionComparison_CoversEveryBehaviorField()
    {
        var baseline = MakeInputs(revision: 1);
        var changes = new[]
        {
            baseline with { ConnectionState = "disconnected", Revision = 2 },
            baseline with { TurnActive = true, Revision = 2 },
            baseline with { CurrentThread = MakeThread("other"), Revision = 2 },
            baseline with { AvailableChannels = new[] { MakeThread("other") }, Revision = 2 },
            baseline with { AvailableModels = new[] { "model" }, Revision = 2 },
            baseline with { ModelChoices = new[] { new ChatModelChoice("model", "Model") }, Revision = 2 },
            baseline with { MessageOptionsDisabled = true, Revision = 2 },
            baseline with
            {
                QueuedMessages = new[]
                {
                    new ChatQueuedMessage("message", "hello", System.DateTimeOffset.UnixEpoch, "nonce"),
                },
                Revision = 2,
            },
            baseline with
            {
                AvailableCommands = new[] { new GatewayCommand { Name = "status" } },
                Revision = 2,
            },
            baseline with { CommandsSupported = true, Revision = 2 },
        };

        Assert.True(baseline.HasSameProjection(baseline with { Revision = 2 }));
        Assert.All(changes, changed => Assert.False(baseline.HasSameProjection(changed)));
    }

    [Fact]
    public void ApplyInputs_AcceptsStrictlyIncreasingRevision()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs(revision: 1, thread: MakeThread("first")));
        vm.ApplyInputs(MakeInputs(revision: 2, thread: MakeThread("second")));

        Assert.Equal("second", vm.Inputs!.CurrentThread.Id);
    }

    [Fact]
    public void CanSend_FalseWhenDraftAndAttachmentsAreEmpty()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs());

        Assert.False(vm.CanSend);
    }

    [Fact]
    public void CanSend_TrueWithNonEmptyDraftWhileConnectedAndIdle()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs());
        vm.SetDraft("hello");

        Assert.True(vm.CanSend);
    }

    [Fact]
    public void CanSend_FalseWhileSending()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs());
        vm.SetDraft("hello");
        vm.SetSending(true);

        Assert.False(vm.CanSend);
    }

    [Fact]
    public void CanSend_FalseWhenDisconnected()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs(connectionState: "disconnected"));
        vm.SetDraft("hello");

        Assert.False(vm.CanSend);
    }

    [Fact]
    public void CanSend_TrueForAttachmentOnlySubmission()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        vm.ApplyInputs(MakeInputs());

        Assert.False(vm.CanSend);
        vm.AddAttachments(new[] { new ChatAttachment { FileName = "a.png" } });
        Assert.True(vm.CanSend);
    }

    [Fact]
    public void Mutations_AreDispatchedThroughIUiDispatcher_WhenOffUiThread()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);

        vm.SetDraft("queued");

        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(1, dispatcher.EnqueuedCount);

        dispatcher.FlushPending();

        Assert.Equal("queued", vm.Draft);
    }

    [Fact]
    public void PropertyChanged_IsRaisedOnEveryAcceptedMutation()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var raisedCount = 0;
        vm.PropertyChanged += (_, _) => raisedCount++;

        vm.SetDraft("a");
        vm.SetDraft("ab");

        Assert.Equal(2, raisedCount);
    }

    [Fact]
    public void Dispose_RejectsLateMutationAndStopsNotifying()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);
        var raisedCount = 0;
        vm.PropertyChanged += (_, _) => raisedCount++;
        vm.SetDraft("before");

        vm.Dispose();
        vm.SetDraft("after");

        Assert.Equal("before", vm.Draft);
        Assert.Equal(1, raisedCount);
        Assert.True(vm.IsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new ChatComposerViewModel(new RecordingUiDispatcher(), initialSpeakerMuted: false);

        vm.Dispose();
        var exception = Record.Exception(vm.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Mutations_QueuedWhileDrainPending_StillApplyInEnqueueOrder()
    {
        // Serialization contract: a mutation that arrives while on the UI thread
        // must not jump ahead of an already-queued, not-yet-drained background
        // mutation. Both are enqueued onto one internal FIFO and applied by the
        // same drain pass, in enqueue order.
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);

        // Background completion enqueues first; the drain is scheduled but held
        // back (RunEnqueuedImmediately is false) until FlushPending() runs it.
        vm.SetDraft("from-background");
        Assert.Equal(1, dispatcher.EnqueuedCount);

        // A "UI thread" mutation arrives before that scheduled drain has run.
        dispatcher.HasThreadAccess = true;
        vm.SetDraft("from-ui-thread-while-pending");

        // It must have deferred to the already-scheduled drain rather than racing
        // ahead: nothing has applied yet, and no second drain was scheduled.
        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(1, dispatcher.EnqueuedCount);

        dispatcher.FlushPending();

        // The single drain applies both mutations in enqueue order; the later one
        // is the final value, exactly as if applied back-to-back in arrival order.
        Assert.Equal("from-ui-thread-while-pending", vm.Draft);
    }

    [Fact]
    public void Mutations_MultipleBackgroundCompletionsQueueBeforeDispatcherRuns_ApplyInOrder()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);

        vm.SetVoiceTranscript("first");
        vm.SetVoiceTranscript("second");
        vm.SetVoiceTranscript("third");

        // All three mutations joined the one scheduled drain; only one drain was
        // ever scheduled with the dispatcher.
        Assert.Equal(1, dispatcher.EnqueuedCount);
        Assert.Null(vm.VoiceTranscript);

        dispatcher.FlushPending();

        Assert.Equal("third", vm.VoiceTranscript);
    }

    [Fact]
    public void Dispose_WithQueuedWork_DrainsWithoutApplyingOrNotifying()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var raisedCount = 0;
        vm.PropertyChanged += (_, _) => raisedCount++;
        var revisionBeforeDispose = vm.RenderRevision;

        vm.SetDraft("queued-before-dispose"); // enqueued; the scheduled drain has not run yet
        vm.Dispose();

        // The already-scheduled drain callback still fires (the dispatcher itself
        // has no idea the view model was disposed), but DrainQueue must observe
        // disposal and drop the queue rather than applying or notifying.
        dispatcher.FlushPending();

        Assert.True(vm.IsDisposed);
        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(0, raisedCount);
        Assert.Equal(revisionBeforeDispose, vm.RenderRevision);
    }

    [Fact]
    public void Mutate_DispatcherRejectsDrain_DropsQueueSafelyAndDoesNotThrow()
    {
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RejectEnqueue = true };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var raisedCount = 0;
        vm.PropertyChanged += (_, _) => raisedCount++;

        var exception = Record.Exception(() => vm.SetDraft("rejected"));

        // A rejected drain must fail safe: no exception, no partial/queued state
        // left behind, no notification for the dropped mutation, and the view
        // model remains usable (not marked disposed) for a future accepted call.
        Assert.Null(exception);
        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(0, raisedCount);
        Assert.False(vm.IsDisposed);

        // Recovery: once the dispatcher accepts work again, new mutations apply
        // normally (the drain-in-progress flag was correctly reset, not left stuck).
        dispatcher.RejectEnqueue = false;
        dispatcher.RunEnqueuedImmediately = false;
        vm.SetDraft("accepted-after-recovery");
        dispatcher.FlushPending();
        Assert.Equal("accepted-after-recovery", vm.Draft);
    }

    [Fact]
    public void DrainQueue_ThrowingPropertyChangedSubscriber_DoesNotWedgeFutureDrains()
    {
        // A single misbehaving PropertyChanged subscriber (or mutation action)
        // must not leave the internal _draining flag stuck true forever, which
        // would silently and permanently freeze every future composer UI update.
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var throwOnce = true;
        vm.PropertyChanged += (_, _) =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("simulated subscriber bug");
            }
        };

        vm.SetDraft("first");
        var exception = Record.Exception(dispatcher.FlushPending);

        // The drain itself must not propagate/crash on the bad subscriber, and the
        // mutation that triggered it still applied (the throw happens only in the
        // notification step, after the state change already ran).
        Assert.Null(exception);
        Assert.Equal("first", vm.Draft);

        // Recovery: a later mutation must still be able to schedule and complete a
        // fresh drain — proving _draining was correctly reset despite the exception.
        vm.SetDraft("second");
        dispatcher.FlushPending();
        Assert.Equal("second", vm.Draft);
    }

    [Fact]
    public void ConcurrentDispose_WaitsForAnAlreadyInFlightApplyToCompleteBeforeReturning()
    {
        // Proves the "apply wins" linearization outcome under REAL concurrency (not
        // same-thread reentrancy): once an apply has begun (acquired the internal
        // apply/lifetime lock), a concurrent Dispose() call on another thread must
        // block until that apply — including raising its PropertyChanged
        // notification — has fully completed before Dispose() can return.
        var dispatcher = new RecordingUiDispatcher();
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var applyStarted = new ManualResetEventSlim(false);
        var releaseApply = new ManualResetEventSlim(false);

        vm.PropertyChanged += (_, _) =>
        {
            if (vm.Draft != "in-flight")
                return;

            applyStarted.Set();
            Assert.True(releaseApply.Wait(TimeSpan.FromSeconds(5)), "Test did not release the blocked apply in time.");
        };

        // HasThreadAccess defaults to true, so Mutate() would drain synchronously
        // on whichever thread calls it — run it on a dedicated thread so the test
        // thread stays free to drive Dispose() concurrently.
        var applyThread = new Thread(() => vm.SetDraft("in-flight"));
        applyThread.Start();
        Assert.True(applyStarted.Wait(TimeSpan.FromSeconds(5)), "Apply did not start in time.");

        var disposeThreadStarted = new ManualResetEventSlim(false);
        var disposeReturned = new ManualResetEventSlim(false);
        var disposeThread = new Thread(() =>
        {
            disposeThreadStarted.Set();
            vm.Dispose();
            disposeReturned.Set();
        });
        disposeThread.Start();

        // Prove the dispose thread has actually started running (not merely
        // scheduled) before asserting it hasn't returned yet — otherwise a
        // starved/not-yet-scheduled thread could make the negative assertion below
        // pass for the wrong reason even against the old, unsynchronized Dispose().
        Assert.True(disposeThreadStarted.Wait(TimeSpan.FromSeconds(5)), "Dispose thread did not start in time.");

        // Dispose must NOT be able to return while the apply is still blocked mid-
        // notification: give it a short window to (incorrectly) race ahead, then
        // prove it has not.
        Assert.False(
            disposeReturned.Wait(TimeSpan.FromMilliseconds(300)),
            "Dispose() returned before the in-flight apply/notification completed — linearization broken.");

        releaseApply.Set();

        Assert.True(disposeReturned.Wait(TimeSpan.FromSeconds(5)), "Dispose() did not complete after the apply finished.");
        Assert.True(applyThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)));

        // The in-flight mutation's state change was applied — it had already
        // started before Dispose() was called, so it completed rather than being
        // dropped.
        Assert.Equal("in-flight", vm.Draft);
        Assert.True(vm.IsDisposed);
    }

    [Fact]
    public void ReentrantDisposeDuringInFlightNotification_DropsQueuedMutationWithNoFurtherStateOrNotificationChange()
    {
        // Proves a reentrant-disposal variant of the "disposal wins" outcome: a
        // mutation that is still only queued (never yet dequeued/applied) when
        // Dispose() takes effect must never be applied — no state change, no
        // revision bump, no notification — here constructed deterministically by
        // enqueuing it and then calling Dispose() reentrantly from inside another
        // mutation's own in-flight PropertyChanged notification. Monitor locks are
        // reentrant for the owning thread, so Dispose()'s disposed-flag flip and
        // queue-clear still run, synchronously, before this method returns.
        //
        // This does NOT exercise the narrower "already dequeued but not yet
        // applied, racing a concurrent Dispose on another thread" gap the apply
        // lock closes — see
        // DequeuedMutationRacingConcurrentDispose_DropsMutationWhenDisposalWinsTheApplyLockRace
        // for a genuinely concurrent, deterministic proof of that specific window.
        var dispatcher = new RecordingUiDispatcher();
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var propertyChangedCount = 0;
        var revisionAfterFirst = -1;

        vm.PropertyChanged += (_, _) =>
        {
            propertyChangedCount++;
            if (vm.Draft != "first")
                return;

            revisionAfterFirst = vm.RenderRevision;

            // While "first" is still being applied/notified (the apply lock is
            // held, reentrantly available only to this same thread), enqueue a
            // second mutation and then dispose. Dispose must drop "second" before
            // the drain can ever reach it again.
            vm.SetDraft("second");
            vm.Dispose();
        };

        vm.SetDraft("first");

        // "first" already completed (its apply had already started) — the apply-
        // wins outcome for the mutation already in flight.
        Assert.Equal("first", vm.Draft);
        Assert.True(vm.IsDisposed);

        // "second" was enqueued after disposal had already begun (from inside
        // "first"'s own notification) and must never be applied or notified: draft,
        // revision, and notification count are all unchanged from immediately
        // after "first" applied.
        Assert.Equal("first", vm.Draft);
        Assert.Equal(revisionAfterFirst, vm.RenderRevision);
        Assert.Equal(1, propertyChangedCount);
    }

    [Fact]
    public void DequeuedMutationRacingConcurrentDispose_DropsMutationWhenDisposalWinsTheApplyLockRace()
    {
        // Proves the exact linearization gap the apply lock closes: a mutation
        // that has already been DEQUEUED (removed from the pending queue, so a
        // queue-clear alone cannot stop it) but has not yet acquired the apply
        // lock, racing a concurrent Dispose() on another thread for that lock. If
        // disposal wins the race, the dequeued mutation must be dropped — no
        // state change, no revision bump, no notification — even though it was
        // already out of the queue when Dispose() ran.
        //
        // The test-only hook is required because this gap is a handful of CPU
        // instructions between releasing _queueLock and acquiring _applyLock: real
        // OS thread scheduling cannot deterministically land inside it, so this
        // uses TestOnlyAfterDequeueBeforeApplyLock to pause the drain thread
        // exactly there while Dispose() races in from a second thread.
        var dispatcher = new RecordingUiDispatcher();
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var initialDraft = vm.Draft;
        var initialRevision = vm.RenderRevision;
        var propertyChangedCount = 0;
        vm.PropertyChanged += (_, _) => propertyChangedCount++;

        var dequeueReached = new ManualResetEventSlim(false);
        var disposeCompleted = new ManualResetEventSlim(false);
        vm.TestOnlyAfterDequeueBeforeApplyLock = () =>
        {
            // Only pause for the mutation under test — avoid recursing if this
            // hook were ever invoked again for a later item.
            vm.TestOnlyAfterDequeueBeforeApplyLock = null;
            dequeueReached.Set();
            Assert.True(disposeCompleted.Wait(TimeSpan.FromSeconds(5)), "Dispose did not complete in time.");
        };

        var drainThread = new Thread(() => vm.SetDraft("second"));
        drainThread.Start();

        Assert.True(dequeueReached.Wait(TimeSpan.FromSeconds(5)), "Drain did not reach the dequeue point in time.");

        // The mutation is now dequeued but paused before the apply lock. Dispose
        // concurrently from a second thread — it must be free to acquire the
        // apply lock immediately (the drain thread is blocked in the test hook,
        // not holding any lock) and win the race.
        vm.Dispose();
        disposeCompleted.Set();

        Assert.True(drainThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(initialDraft, vm.Draft);
        Assert.Equal(initialRevision, vm.RenderRevision);
        Assert.Equal(0, propertyChangedCount);
        Assert.True(vm.IsDisposed);
    }

    [Fact]
    public async Task ReentrantPropertyChangedSubscriber_EnqueuesAnotherMutation_NoDeadlockAndLaterMutationDrains()
    {
        // A PropertyChanged subscriber that reentrantly calls back into the view
        // model (enqueuing another mutation) must not deadlock — Mutate() only
        // ever needs the queue lock, which the apply lock holder never reacquires
        // reentrantly for this — and the reentrantly-enqueued mutation must still
        // drain and apply within the same overall drain pass.
        var dispatcher = new RecordingUiDispatcher();
        var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
        var reentered = false;

        vm.PropertyChanged += (_, _) =>
        {
            if (!reentered && vm.Draft == "first")
            {
                reentered = true;
                vm.SetDraft("second");
            }
        };

        var driverTask = Task.Run(() => vm.SetDraft("first"));
        var completed = await Task.WhenAny(driverTask, Task.Delay(TimeSpan.FromSeconds(5))) == driverTask;

        Assert.True(completed, "Reentrant PropertyChanged subscriber caused a deadlock.");
        Assert.True(reentered);
        // The later, reentrantly-enqueued mutation drained and applied within the
        // same pass — it was not dropped or left stranded in the queue.
        Assert.Equal("second", vm.Draft);
    }

    [Fact]
    public void StressDisposeAcrossThreads_OffThreadMutationsNeverApplyOrNotifyAfterDisposalWithoutRelyingOnBlockingEventSynchronization()
    {
        // Regression coverage for cross-lock/no-lock visibility of the disposal
        // flag: _disposed is written under _applyLock (in Dispose) but read under
        // _queueLock (Mutate's authoritative check), under _applyLock (DrainQueue's
        // linearization recheck), and under no lock at all (Mutate's unlocked
        // fast-path check, and IsDisposed). A Monitor's acquire/release memory
        // barrier only orders memory for threads that actually enter *that same*
        // monitor — a plain (non-volatile) bool write in Dispose is not guaranteed
        // to ever become visible to a thread that only ever reads it via a
        // different lock or no lock at all. _disposed is now `volatile`
        // specifically to close that gap.
        //
        // This test deliberately avoids ManualResetEventSlim or any other
        // blocking/kernel synchronization primitive to publish "Dispose already
        // returned" to the racing thread — those carry their own full memory
        // fences and would mask a missing `volatile` regardless of the real fix.
        // The only cross-thread signal it uses is a plain Volatile.Read/Write on a
        // local int (not a lock, not a wait handle, not the field under test)
        // driving a tight, lock-free polling loop that hammers real Mutate() calls
        // across many iterations, so a genuine visibility failure has a realistic
        // chance to manifest as a mutation applying/notifying after Dispose() has
        // already returned on another thread.
        const int iterations = 1000;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var dispatcher = new RecordingUiDispatcher();
            var vm = new ChatComposerViewModel(dispatcher, initialSpeakerMuted: false);
            var disposeReturned = 0; // 0/1, touched only via Volatile.Read/Write.
            var violationObserved = false;

            vm.PropertyChanged += (_, _) =>
            {
                if (Volatile.Read(ref disposeReturned) != 0)
                    violationObserved = true;
            };

            var mutatorThread = new Thread(() =>
            {
                var spins = 0;
                while (Volatile.Read(ref disposeReturned) == 0 && spins < 200_000)
                {
                    vm.SetDraft("stress-" + spins);
                    spins++;
                }
            });

            mutatorThread.Start();
            vm.Dispose();
            Volatile.Write(ref disposeReturned, 1);

            Assert.True(
                mutatorThread.Join(TimeSpan.FromSeconds(5)),
                $"Mutator thread did not finish (iteration {iteration}).");
            Assert.False(
                violationObserved,
                $"A mutation applied/notified after Dispose() had already returned on another thread (iteration {iteration}).");
            Assert.True(vm.IsDisposed);
        }
    }

    [Fact]
    public void Disposed_FieldIsDeclaredVolatile()
    {
        // The stress test above proves the *externally observable* disposal
        // linearization holds, but that property is also (separately) guaranteed
        // by the _applyLock monitor for every code path that actually goes
        // through it — so a passing stress test alone does not discriminate
        // whether `_disposed` is actually declared `volatile`. This structural
        // guard closes that gap directly: it fails if a future edit ever removes
        // the `volatile` keyword, which is the only thing that also protects the
        // *unlocked* fast-path read in Mutate() and the unlocked IsDisposed
        // property from cross-thread staleness.
        var field = typeof(ChatComposerViewModel).GetField(
            "_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Contains(typeof(IsVolatile), field!.GetRequiredCustomModifiers());
    }
}

using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

/// <summary>
/// Regression coverage for the root cause behind the camera.snap/screen.snapshot
/// MCP hang: after 31a8c655/16cb6897 gated those captures behind
/// EnsureRecordingConsentAsync, an unattended caller (local MCP HTTP client,
/// automated agent, hosted CI) had no way to answer the interactive consent
/// window, so the request hung until the caller's own HTTP timeout fired.
/// ConsentPromptTimeoutGate is the fail-closed fix: it bounds the wait and
/// always resolves to "denied" on timeout, never "granted".
/// </summary>
public sealed class ConsentPromptTimeoutGateTests
{
    [Fact]
    public async Task WithTimeout_DeniesAndInvokesCallback_WhenNoResponseInTime()
    {
        // Nothing ever completes this - simulates an unattended consent window
        // (no human present to click Allow/Deny), the exact shape of the local
        // MCP integration test hang.
        var neverAnswered = new TaskCompletionSource<bool>();
        var timedOutCallbackFired = false;

        var result = await ConsentPromptTimeoutGate.WithTimeout(
            neverAnswered.Task,
            TimeSpan.FromMilliseconds(50),
            onTimedOut: () => timedOutCallbackFired = true);

        // Fail closed: a timeout is always a denial, never an implicit grant.
        Assert.False(result);
        Assert.True(timedOutCallbackFired);
    }

    [Fact]
    public async Task WithTimeout_ReturnsGrantedResult_WhenAnsweredBeforeTimeout()
    {
        var answered = new TaskCompletionSource<bool>();
        answered.SetResult(true);
        var timedOutCallbackFired = false;

        var result = await ConsentPromptTimeoutGate.WithTimeout(
            answered.Task,
            TimeSpan.FromSeconds(30),
            onTimedOut: () => timedOutCallbackFired = true);

        Assert.True(result);
        Assert.False(timedOutCallbackFired);
    }

    [Fact]
    public async Task WithTimeout_ReturnsDeniedResult_WhenAnsweredBeforeTimeout()
    {
        var answered = new TaskCompletionSource<bool>();
        answered.SetResult(false);
        var timedOutCallbackFired = false;

        var result = await ConsentPromptTimeoutGate.WithTimeout(
            answered.Task,
            TimeSpan.FromSeconds(30),
            onTimedOut: () => timedOutCallbackFired = true);

        Assert.False(result);
        Assert.False(timedOutCallbackFired);
    }

    [Fact]
    public async Task WithTimeout_StillResolvesToDenied_AfterCallerAbandonsWaitAsync_BeforeInternalTimeoutElapses()
    {
        // NodeService.EnsureCaptureConsentAsync shares a single prompt across
        // every concurrent caller of the same consent type
        // (_screen/_camera/_locationConsentInFlight). If the request that
        // originally created the prompt is canceled (its own caller
        // disconnects) before the consent timeout elapses, a *different*
        // concurrent caller may still be relying on this same task to
        // eventually resolve. The bounded task must NOT be torn down just
        // because one caller's own cancellation wrapper stopped waiting on
        // it - it must keep running and still fail-closed once its own
        // timeout elapses, exactly like the abandoned-prompt path in
        // NodeService.CompleteAbandonedConsentPromptAsync depends on.
        var neverAnswered = new TaskCompletionSource<bool>();
        var timedOutCallbackFired = false;

        var bounded = ConsentPromptTimeoutGate.WithTimeout(
            neverAnswered.Task,
            TimeSpan.FromMilliseconds(150),
            onTimedOut: () => timedOutCallbackFired = true);

        using var ownerCts = new CancellationTokenSource();
        ownerCts.Cancel();

        // The owning caller's own wrapper observes cancellation immediately...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bounded.WaitAsync(ownerCts.Token));

        // ...but the shared bounded task itself is unaffected by that
        // caller's cancellation and still fail-closes to false once its own
        // internal timeout elapses, so any other waiter sharing this task
        // still gets a bounded, well-formed denial instead of hanging.
        var result = await bounded;
        Assert.False(result);
        Assert.True(timedOutCallbackFired);
    }
}

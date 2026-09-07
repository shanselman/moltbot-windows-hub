namespace OpenClawTray.Services;

/// <summary>
/// Bounds how long a capture consent prompt (screen/camera/location) may wait
/// for a human response. Fails closed: if nobody answers within
/// <paramref name="timeout"/> (in <see cref="WithTimeout"/>), the result is
/// treated as "denied", never "granted". This is what stops an unattended
/// caller - a local MCP HTTP client, an automated agent, a hosted test with
/// no human at the keyboard - from hanging forever on a consent window
/// nobody can answer, while still letting a real interactive user take their
/// time to decide (up to the configured timeout) without ever being silently
/// bypassed.
///
/// The returned task is bound only by <paramref name="timeout"/> and
/// <paramref name="promptTask"/> itself - it deliberately does not accept a
/// caller <see cref="CancellationToken"/>. Capture consent prompts are
/// shared across concurrent callers of the same type
/// (NodeService's _screen/_camera/_locationConsentInFlight), so the timeout
/// must keep running even if the request that originally created the prompt
/// is canceled or disconnects; a caller that wants to additionally honor its
/// own cancellation should layer `.WaitAsync(cancellationToken)` on top of
/// the task this method returns, which lets that caller stop waiting without
/// tearing down the shared timeout for any other waiter.
/// </summary>
internal static class ConsentPromptTimeoutGate
{
    public static async Task<bool> WithTimeout(
        Task<bool> promptTask,
        TimeSpan timeout,
        Action onTimedOut)
    {
        ArgumentNullException.ThrowIfNull(promptTask);
        ArgumentNullException.ThrowIfNull(onTimedOut);

        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(promptTask, timeoutTask).ConfigureAwait(false);
        if (completed == promptTask)
        {
            return await promptTask.ConfigureAwait(false);
        }

        onTimedOut();
        return false;
    }
}

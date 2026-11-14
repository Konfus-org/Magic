using Magic.Threading;

namespace Magic.DeferredRendering;

/// <summary>
/// The wait of a world that arrived for what a gem draws of it to settle.
/// </summary>
/// <remarks>
/// The gem returns <see cref="WaitAsync"/> from its <c>OnDomainArrivedAsync</c>. The frame that settles its picture,
/// or that draws none, calls <see cref="End"/>.
/// </remarks>
public sealed class SettleWait(Threads threads)
{
    /// <summary>
    /// The wait of the load that arrived last, none before the first. Render thread.
    /// </summary>
    private TaskCompletionSource? _waiting;

    /// <summary>
    /// Waits until <see cref="End"/>, or until <paramref name="cancel"/>. It takes the place of the wait of the load
    /// before, so a frame cannot end this one for that one.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancel)
    {
        await threads.Enter(EngineThread.Render);
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting = waiting;
        await waiting.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the wait of the load that arrived last, when it still runs. Render thread.
    /// </summary>
    public void End() => _waiting?.TrySetResult();
}

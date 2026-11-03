using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Magic.Debugging;

namespace Magic.Threading;

/// <summary>
/// The engine's threads, as somewhere work can be posted (<see cref="Post"/>) or an awaited flow can step onto:
/// <c>await threads.Enter(EngineThread.Main)</c> goes on there, as that thread next pumps. For a flow whose steps must be
/// there, such as an awaited pipeline. Any thread, but where a member says otherwise.
/// </summary>
public sealed class Threads
{
    /// <summary>
    /// By <see cref="EngineThread"/>: which thread it is, and what was posted to it.
    /// </summary>
    private readonly int[] _threadIds;
    private readonly Channel<Action>[] _posted;

    /// <summary>
    /// Made on the main thread, which it takes as <see cref="EngineThread.Main"/>.
    /// </summary>
    internal Threads()
    {
        int count = Enum.GetValues<EngineThread>().Length;
        _threadIds = new int[count];
        _threadIds[(int)EngineThread.Main] = Environment.CurrentManagedThreadId;
        _posted = new Channel<Action>[count];
        for (int i = 0; i < count; i++)
        {
            _posted[i] = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        }
    }

    public bool IsCurrent(EngineThread thread) => Environment.CurrentManagedThreadId == _threadIds[(int)thread];

    /// <summary>
    /// What to await to go on on <paramref name="thread"/>; nothing to wait for when already on it.
    /// </summary>
    public ThreadEntry Enter(EngineThread thread) => new(this, thread);

    /// <summary>
    /// Has <paramref name="work"/> run on <paramref name="thread"/> as it next pumps. Any thread.
    /// </summary>
    public void Post(EngineThread thread, Action work) => _posted[(int)thread].Writer.TryWrite(work);

    /// <summary>
    /// Runs what was posted to <paramref name="thread"/>, and the flows that stepped onto it, each until it next leaves.
    /// One that throws is logged, and the rest still run. On that thread.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever is posted is logged if it throws; one failure mustn't stop the thread."
    )]
    internal void Pump(EngineThread thread)
    {
        while (_posted[(int)thread].Reader.TryRead(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error($"What was posted to the {thread} thread threw: {ex}");
            }
        }
    }
}

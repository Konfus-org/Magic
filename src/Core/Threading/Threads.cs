using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Magic.Debugging;

namespace Magic.Threading;

/// <summary>
/// The engine's threads, as somewhere work can be posted (<see cref="Post"/>) or an awaited flow can step onto:
/// <c>await threads.Enter(EngineThread.Game)</c> goes on there, as that thread next pumps. For a flow whose steps must be
/// there, such as an awaited pipeline. It makes and ends the engine's threads but the game thread
/// (<see cref="Start"/>). Any thread, but where a member says otherwise.
/// </summary>
public sealed class Threads
{
    /// <summary>
    /// By <see cref="EngineThread"/>: which thread it is, and what was posted to it.
    /// </summary>
    private readonly int[] _threadIds;

    private readonly Channel<Action>[] _posted;

    /// <summary>
    /// By <see cref="EngineThread"/>: the thread <see cref="Start"/> made for it, until <see cref="Stop"/> takes it away.
    /// </summary>
    private readonly Thread?[] _running;

    /// <summary>
    /// Made on the process's first thread, which it names and takes as the game thread, and as every engine thread
    /// until one is started.
    /// </summary>
    internal Threads()
    {
        Thread.CurrentThread.Name ??= nameof(EngineThread.Game);
        int count = Enum.GetValues<EngineThread>().Length;
        _threadIds = new int[count];
        Array.Fill(_threadIds, Environment.CurrentManagedThreadId);
        _running = new Thread?[count];
        _posted = new Channel<Action>[count];
        for (int i = 0; i < count; i++)
        {
            _posted[i] = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        }
    }

    public bool IsCurrent(EngineThread thread) =>
        Environment.CurrentManagedThreadId == Volatile.Read(ref _threadIds[(int)thread]);

    /// <summary>
    /// What to await to go on on <paramref name="thread"/>; nothing to wait for when already on it.
    /// </summary>
    public ThreadEntry Enter(EngineThread thread) => new(this, thread);

    /// <summary>
    /// Has <paramref name="work"/> run on <paramref name="thread"/> as it next pumps. Any thread.
    /// </summary>
    public void Post(EngineThread thread, Action work) => _posted[(int)thread].Writer.TryWrite(work);

    /// <summary>
    /// Makes <paramref name="thread"/> a thread of its own, which runs what was posted to it, then
    /// <paramref name="frame"/>, again and again until <see cref="Stop"/>. It is that thread from now on.
    /// </summary>
    internal void Start(EngineThread thread, Action frame)
    {
        Thread made = new(() => Run(thread, frame)) { Name = thread.ToString(), IsBackground = true };
        Volatile.Write(ref _threadIds[(int)thread], made.ManagedThreadId);
        Volatile.Write(ref _running[(int)thread], made);
        made.Start();
    }

    /// <summary>
    /// Ends the thread <see cref="Start"/> made for <paramref name="thread"/> once its frame is done, and waits for it:
    /// the calling thread is <paramref name="thread"/> from then on, as for what ends after it.
    /// </summary>
    internal void Stop(EngineThread thread)
    {
        Thread running =
            Interlocked.Exchange(ref _running[(int)thread], null)
            ?? throw new InvalidOperationException($"The {thread} thread was never started.");

        // A wait at a boundary: once joined, the thread has ended and holds nothing up.
        running.Join();
        Volatile.Write(ref _threadIds[(int)thread], Environment.CurrentManagedThreadId);
    }

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

    /// <summary>
    /// The loop of a thread <see cref="Start"/> made, for as long as it is still the one running <paramref name="thread"/>.
    /// </summary>
    private void Run(EngineThread thread, Action frame)
    {
        while (Volatile.Read(ref _running[(int)thread]) == Thread.CurrentThread)
        {
            Pump(thread);
            frame();
        }
    }
}

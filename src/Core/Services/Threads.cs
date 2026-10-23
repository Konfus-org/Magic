using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Magic.Contexts.Telemetry;
using Magic.Contexts.Threading;
using Magic.Extensions;

namespace Magic.Services;

/// <summary>
/// Where work runs when it has to run somewhere in particular: on one of the engine's two threads
/// (<see cref="EngineThread"/>), each of which runs what it is handed in the order it was handed, or on a thread of its
/// own for work that lasts as long as the run. Ordinary async and background work is plain tasks on .NET's thread pool,
/// not this. Safe from any thread.
/// <para>
/// The main thread runs what it was handed at the start of every frame, before Update; the render thread between
/// frames. Nothing here waits for another thread: <see cref="PostAsync(EngineThread, Action, CancellationToken)"/>
/// hands back a task, so the two engine threads never wait on each other but where the frame loop says. Work handed to
/// the main thread before it starts runs on its first frame.
/// </para>
/// <para>
/// What a worker makes for a gem is not handed over as work: it is data the gem keeps and takes in at the start of its
/// own hook. <see cref="PostAsync{T}(EngineThread, Func{T}, CancellationToken)"/> is for a flow that must await a
/// step on an engine thread and what it returns. When the frames end, the main thread runs what it was left and takes
/// no more, the gems having stopped on it; then the render thread runs what it was left, takes no more, and the gems
/// are disposed on it. Anything handed to either after that is dropped.
/// </para>
/// </summary>
public sealed class Threads : IDisposable
{
    private readonly App _app;
    private readonly WorkQueue _main = new("main");
    private readonly WorkQueue _render = new("render");

    /// <summary>
    /// Makes the calling thread, the process's first, the render thread.
    /// </summary>
    internal Threads(App app)
    {
        _app = app;
        _render.Owner = Environment.CurrentManagedThreadId;
        Thread.CurrentThread.Name ??= "Render";
    }

    public void Dispose()
    {
        _main.Dispose();
        _render.Dispose();
    }

    /// <summary>
    /// Whether the calling thread may do <paramref name="thread"/>'s work now: it is that thread, or that thread is not
    /// running (start-up and shutdown, which run alone on the render thread).
    /// </summary>
    public bool IsCurrent(EngineThread thread)
    {
        int owner = Queue(thread).Owner;

        return owner == 0 || owner == Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Hands <paramref name="work"/> to <paramref name="thread"/>; the task ends once it has run, with what it threw,
    /// and whoever awaits it goes on on a worker. Work that has not started when <paramref name="cancel"/> is
    /// cancelled, or that the thread will never run because the run has ended, never runs, and the task ends cancelled.
    /// </summary>
    public Task PostAsync(EngineThread thread, Action work, CancellationToken cancel = default)
    {
        return PostAsync<object?>(
            thread,
            () =>
            {
                work();

                return null;
            },
            cancel);
    }

    /// <inheritdoc cref="PostAsync(EngineThread, Action, CancellationToken)"/>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "What the work throws ends the task, for whoever awaits it.")]
    public Task<T> PostAsync<T>(EngineThread thread, Func<T> work, CancellationToken cancel = default)
    {
        if (cancel.IsCancellationRequested) return Task.FromCanceled<T>(cancel);

        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Queue(thread).TryEnqueue(() =>
        {
            if (cancel.IsCancellationRequested)
            {
                done.SetCanceled(cancel);

                return;
            }

            try
            {
                done.SetResult(work());
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                done.SetCanceled(cancel);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        if (!queued) done.SetCanceled(CancellationToken.None);

        return done.Task;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own named <paramref name="name"/>: for work that lasts as long
    /// as the run and would hold a pool thread the whole time, such as reading the terminal. It is given
    /// <see cref="App.Quitting"/>, and the task ends with it: with what it threw, or cancelled when it stops for the
    /// token. The thread is a background one, so work that cannot look at the token (a blocking read) does not keep the
    /// process alive.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "What the work throws ends the task, for whoever awaits it.")]
    public Task RunAsync(string name, Action<CancellationToken> work)
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken quitting = _app.Quitting;
        Thread thread = new(() =>
        {
            try
            {
                work(quitting);
                ended.SetResult();
            }
            catch (OperationCanceledException) when (quitting.IsCancellationRequested)
            {
                ended.SetCanceled(quitting);
            }
            catch (Exception ex)
            {
                ended.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();

        return ended.Task;
    }

    /// <summary>
    /// Hands <paramref name="work"/> to <paramref name="thread"/>, in its turn after the work handed before it, with no
    /// task and not timed as work: for the engine's own hand-offs, such as a frame's Render, whose parts are timed
    /// themselves. False once the thread has ended, and the work is dropped.
    /// </summary>
    internal bool TryPost(EngineThread thread, Action work) => Queue(thread).TryEnqueue(work, isTimed: false);

    /// <summary>
    /// Runs what <paramref name="thread"/> was handed so far. On that thread: the main thread drains its work at the
    /// start of each frame.
    /// </summary>
    internal void Drain(EngineThread thread) => Queue(thread).RunPending();

    /// <summary>
    /// Runs <paramref name="frames"/> on a new thread, the main thread, and the render thread's work on the calling
    /// thread, the render thread, until <paramref name="frames"/> returns: the main thread then runs what it was left,
    /// takes no more and hands the render thread its end. Then <paramref name="end"/> runs here, once the render thread
    /// has run what it was left and takes no more.
    /// </summary>
    internal void Run(Action frames, Action end)
    {
        Thread main = new(() => RunMain(frames)) { Name = "Main" };
        main.Start();
        _render.RunUntilEnded();
        end();
        main.Join();

        // Only now: while the gems dispose, the main thread's is still nobody else's.
        _main.Owner = 0;
    }

    private void RunMain(Action frames)
    {
        _main.Owner = Environment.CurrentManagedThreadId;
        try
        {
            frames();
        }
        finally
        {
            // Nothing more is taken, then what came before that runs; the render thread's end is handed last, so
            // everything handed to it before the frames ended still runs.
            _main.End();
            _main.RunPending();
            _render.TryEnqueue(_render.End, isTimed: false);
        }
    }

    private WorkQueue Queue(EngineThread thread)
    {
        return thread switch
        {
            EngineThread.Main => _main,
            EngineThread.Render => _render,
            EngineThread.Unknown => throw new ArgumentOutOfRangeException(
                nameof(thread),
                thread,
                "There is no such engine thread."),
            _ => throw new ArgumentOutOfRangeException(nameof(thread), thread, "There is no such engine thread."),
        };
    }

    /// <summary>
    /// One engine thread's work, in the order it came, and the thread running it. Each piece of work is timed while
    /// something listens, as a gem's hooks are: what is handed to a thread between frames stalls the frame as surely as
    /// a hook does.
    /// </summary>
    private sealed class WorkQueue(string name) : IDisposable
    {
        private static readonly Histogram<double> _workDuration = Metrics.Meter.CreateTimer(
            Metrics.WorkDuration,
            "How long one piece of work handed to an engine thread took, tagged with the thread.",
            Metrics.FrameBuckets);

        // Completed when the thread ends: a write after that fails, so nothing is left behind unrun.
        private readonly Channel<Pending> _work = Channel.CreateUnbounded<Pending>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly ManualResetEventSlim _arrived = new();

        /// <summary>
        /// The managed id of the thread that runs this work; 0 while none does.
        /// </summary>
        public int Owner
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public void Dispose() => _arrived.Dispose();

        /// <summary>
        /// Adds <paramref name="work"/>, timed unless <paramref name="isTimed"/> says not; false once the thread has
        /// ended.
        /// </summary>
        public bool TryEnqueue(Action work, bool isTimed = true)
        {
            if (!_work.Writer.TryWrite(new Pending(work, isTimed))) return false;

            _arrived.Set();

            return true;
        }

        /// <summary>
        /// Blocks until work arrives or the queue ends (<see cref="End"/>).
        /// </summary>
        public void Wait()
        {
            _arrived.Wait();

            // Before running what came: work added from here on sets it again.
            _arrived.Reset();
        }

        public void RunPending()
        {
            while (_work.Reader.TryRead(out Pending pending))
            {
                Run(pending);
            }
        }

        /// <summary>
        /// Takes no more work, and wakes the thread running it to run what is left.
        /// </summary>
        public void End()
        {
            _work.Writer.TryComplete();
            _arrived.Set();
        }

        /// <summary>
        /// Runs work as it comes until <see cref="End"/> and the last of it has run. On the thread that runs this work.
        /// </summary>
        public void RunUntilEnded()
        {
            while (!_work.Reader.Completion.IsCompleted)
            {
                Wait();
                RunPending();
            }
        }

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Whatever posted work throws, it is logged and the thread goes on.")]
        private void Run(in Pending pending)
        {
            long start = pending.IsTimed ? _workDuration.Start() : 0;
            try
            {
                pending.Work();
            }
            catch (Exception ex)
            {
                Debugging.Error($"Work handed to the {name} thread threw: {ex}");
            }

            // Outside the try: a listener that throws is its own failure, not the work's.
            _workDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.ThreadNameTag, name));
        }

        /// <summary>
        /// Work in the queue, and whether it is timed as work: a frame's Render is not, as its hooks are timed each.
        /// </summary>
        private readonly record struct Pending(Action Work, bool IsTimed);
    }
}

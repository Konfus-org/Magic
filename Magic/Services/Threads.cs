using Magic.Contexts.Threading;
using Magic.Utils;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Services;

/// <summary>
/// Where work runs, and every call says where. Work for <see cref="ThreadId.Worker"/> goes to whichever worker is
/// free first, which is .NET's own thread pool: the engine keeps no workers. Work for a dedicated thread runs on
/// that one thread, in the order it was handed over: <see cref="ThreadId.Main"/> (the simulation: the ECS, scripts,
/// the debug UI), <see cref="ThreadId.Render"/> (the GPU and the windows, which the OS ties to one thread) or a
/// thread made by <see cref="Create"/>. The host says which thread is which (<see cref="Claim"/>,
/// <see cref="StartAsync"/>); a dedicated thread nobody has claimed takes its work on the calling thread, so start-up,
/// shutdown and tests need no second thread. Any thread.
/// </summary>
public sealed class Threads : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Dedicated> _dedicated = [];
    private readonly List<Thread> _created = [];
    private readonly CancellationTokenSource _disposed = new();

    public Threads()
    {
        // IMPORTANT: Always add in this order, 1 for main and 2 for render, so the engine can assume those ids.
        ThreadId main = Add();
        ThreadId render = Add();
        Debugging.Assert(main == ThreadId.Main && render == ThreadId.Render, "The main and render threads must be the first two.");
    }

    public void Dispose()
    {
        _disposed.Cancel();

        Thread[] created;
        Dedicated[] dedicated;
        lock (_lock)
        {
            created = [.. _created];
            dedicated = [.. _dedicated];
        }

        // A created thread ends once it is cancelled; nothing waits on a queue after that.
        foreach (Thread thread in created)
        {
            if (thread != Thread.CurrentThread)
                thread.Join();
        }

        foreach (Dedicated queue in dedicated)
            queue.Dispose();

        _disposed.Dispose();
    }

    /// <summary>
    /// Starts another dedicated thread, which runs what it is handed until the engine goes.
    /// </summary>
    public ThreadId Create(string name)
    {
        ThreadId id = Add();
#pragma warning disable RS0030 // this is the service that makes threads
        Thread thread = new(() => RunUntil(id, _disposed.Token)) { Name = name, IsBackground = true };
#pragma warning restore RS0030
        lock (_lock)
            _created.Add(thread);

        thread.Start();
        SpinWait.SpinUntil(() => Find(id).Owner != 0); // from here on its work is its own, not the caller's

        return id;
    }

    /// <summary>
    /// Is the calling thread the one <paramref name="thread"/> names? For <see cref="ThreadId.Worker"/>: is it a worker?
    /// </summary>
    public bool IsCurrent(ThreadId thread)
    {
        return thread == ThreadId.Worker ? Thread.CurrentThread.IsThreadPoolThread : Find(thread).Owner == Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Hands the work over and returns at once. What it throws is logged.
    /// </summary>
    public void Post(ThreadId thread, Action work)
    {
        if (thread == ThreadId.Worker)
            ThreadPool.QueueUserWorkItem(RunLogged, work, preferLocal: false);
        else
            Find(thread).Enqueue(() => RunLogged(work));
    }

    /// <summary>
    /// Runs the work on the thread and returns when it has; what it throws is thrown here. On the calling thread
    /// when that is the thread, or for <see cref="ThreadId.Worker"/>: waiting for a worker is no faster.
    /// </summary>
    public void Invoke(ThreadId thread, Action work)
    {
        Invoke<object?>(thread, () =>
        {
            work();
            return null;
        });
    }

    /// <inheritdoc cref="Invoke(ThreadId, Action)"/>
    public T Invoke<T>(ThreadId thread, Func<T> work)
    {
        if (thread == ThreadId.Worker)
            return work();

        Dedicated target = Find(thread);
        if (target.Owner == 0 || target.Owner == Environment.CurrentManagedThreadId)
            return work();

        TaskCompletionSource<T> done = new();
        target.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Hands the work over; the task ends when it has run, with what it threw. Whoever awaits it continues on any
    /// thread. Work that has not started when <paramref name="cancel"/> is cancelled never runs, and the task ends
    /// cancelled. Work that has started is handed <paramref name="cancel"/> and stops for it by throwing
    /// <see cref="OperationCanceledException"/>, which ends the task cancelled too; work that does not look at it
    /// runs to its end.
    /// </summary>
    public Task InvokeAsync(ThreadId thread, Action<CancellationToken> work, CancellationToken cancel = default)
    {
        return InvokeAsync<object?>(thread, cancel =>
        {
            work(cancel);
            return null;
        }, cancel);
    }

    /// <inheritdoc cref="InvokeAsync(ThreadId, Action{CancellationToken}, CancellationToken)"/>
    public Task<T> InvokeAsync<T>(ThreadId thread, Func<CancellationToken, T> work, CancellationToken cancel = default)
    {
        if (thread == ThreadId.Worker)
            return Task.Run(() => work(cancel), cancel);
        if (cancel.IsCancellationRequested)
            return Task.FromCanceled<T>(cancel);

        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Find(thread).Enqueue(() =>
        {
            if (cancel.IsCancellationRequested)
            {
                done.SetCanceled(cancel);
                return;
            }

            try
            {
                done.SetResult(work(cancel));
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

        return done.Task;
    }

    /// <summary>
    /// Starts a thread of its own for <paramref name="thread"/> with <paramref name="body"/> as all it does: the
    /// thread is claimed before the body runs and nobody's again once it ends. The task ends with the body, with
    /// what it threw. Work handed to the thread waits for the body to <see cref="RunPending"/>.
    /// </summary>
    internal Task StartAsync(ThreadId thread, Action body)
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable RS0030 // this is the service that makes threads
        Thread started = new(() =>
#pragma warning restore RS0030
        {
            Claim(thread);
            try
            {
                body();
                Release(thread);
                ended.SetResult();
            }
            catch (Exception ex)
            {
                Release(thread);
                ended.SetException(ex);
            }
        })
        {
            Name = thread == ThreadId.Main ? "Main" : $"Thread {thread.Value}",
            Priority = ThreadPriority.AboveNormal, // a thread a frame waits on goes before the workers, which load on every core there is
        };
        started.Start();

        return ended.Task;
    }

    /// <summary>
    /// Makes the calling thread the one <paramref name="thread"/> names: from here on work for it waits for this thread to run it.
    /// </summary>
    internal void Claim(ThreadId thread)
    {
        Find(thread).Owner = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Runs what <paramref name="thread"/> was handed so far and returns. On the thread that claimed it.
    /// </summary>
    internal void RunPending(ThreadId thread)
    {
        Dedicated target = Find(thread);
        while (target.TryDequeue(out Action? work))
            work();
    }

    /// <summary>
    /// Makes the calling thread the one <paramref name="thread"/> names and runs what it is handed until <paramref name="stop"/>; then the thread is nobody's again.
    /// </summary>
    internal void RunUntil(ThreadId thread, CancellationToken stop)
    {
        Claim(thread);
        Dedicated target = Find(thread);
        using CancellationTokenRegistration wake = stop.Register(() => target.Wake());
        while (!stop.IsCancellationRequested)
        {
            target.WaitForWork();
            while (target.TryDequeue(out Action? work))
                work();
        }

        Release(thread);
    }

    /// <summary>
    /// Makes the calling thread the one <paramref name="thread"/> names and runs what it is handed until
    /// <paramref name="until"/> has ended; then the thread is nobody's again.
    /// </summary>
    internal void RunUntil(ThreadId thread, Task until)
    {
        using CancellationTokenSource ended = new();
        Task signalled = until.ContinueWith(_ => ended.Cancel(), TaskScheduler.Default);

        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; // as in Start: a frame waits on this thread too
        RunUntil(thread, ended.Token);
        signalled.Wait(ended.Token); // nothing cancels the source after it is disposed
    }

    /// <summary>
    /// The thread that ran <paramref name="thread"/> has ended: what is left for it, and whatever comes, runs on whoever hands it over.
    /// </summary>
    internal void Release(ThreadId thread)
    {
        Dedicated target = Find(thread);
        target.Owner = 0;
        while (target.TryDequeue(out Action? work))
            work();
    }

    private ThreadId Add()
    {
        lock (_lock)
        {
            _dedicated.Add(new Dedicated());
            return new ThreadId(_dedicated.Count);
        }
    }

    private Dedicated Find(ThreadId thread)
    {
        lock (_lock)
        {
            return thread.Value > 0 && thread.Value <= _dedicated.Count
                ? _dedicated[thread.Value - 1]
                : throw new ArgumentException($"No thread has id {thread.Value}.", nameof(thread));
        }
    }

    private static void RunLogged(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Error($"Posted work threw: {ex}");
        }
    }

    /// <summary>
    /// One dedicated thread's work, in the order it came.
    /// </summary>
    private sealed class Dedicated : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Queue<Action> _work = new();
        private readonly SemaphoreSlim _waiting = new(0);
        private int _owner;

        /// <summary>
        /// The managed id of the thread running this one's work; 0 while none is.
        /// </summary>
        public int Owner
        {
            get
            {
                lock (_lock)
                {
                    return _owner;
                }
            }
            set
            {
                lock (_lock)
                    _owner = value;
            }
        }

        public void Dispose()
        {
            _waiting.Dispose();
        }

        public void Enqueue(Action work)
        {
            lock (_lock)
            {
                if (_owner != 0)
                {
                    _work.Enqueue(work);
                    _waiting.Release();
                    return;
                }
            }

            // Nobody runs this thread (before the loop starts, after it ends, in a test): the caller does.
            work();
        }

        public bool TryDequeue([NotNullWhen(true)] out Action? work)
        {
            lock (_lock)
            {
                return _work.TryDequeue(out work);
            }
        }

        public void WaitForWork()
        {
            _waiting.Wait();
        }

        public void Wake()
        {
            _waiting.Release();
        }
    }
}

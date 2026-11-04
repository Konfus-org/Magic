using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Magic.Debugging;
using Magic.Pipelines;
using Magic.Utils;

namespace Magic.Events;

/// <summary>
/// Messages between gems, by type. Anything publishes one, from any thread, and every gem hears it the next frame:
/// read from that frame's <see cref="Frame.Events"/> in a hook, or handed to a watcher as the frame starts, before any
/// gem's hook. A message is a struct, and publishing, reading and watching allocate nothing once its
/// type's buffers have grown.
/// </summary>
public sealed class EventCoordinator
{
    private readonly ConcurrentDictionary<Type, IEventQueue> _queues = new();
    private readonly Lock _adding = new();

    /// <summary>
    /// Every queue in <see cref="_queues"/>, replaced whole as one is added, so a take walks them without allocating.
    /// </summary>
    private IEventQueue[] _all = [];

    /// <summary>
    /// Hands <paramref name="message"/> to every gem in the next frame. Any thread.
    /// </summary>
    public void Publish<T>(in T message)
        where T : struct => GetQueue<T>().Published.Enqueue(message);

    /// <summary>
    /// Calls <paramref name="watcher"/> with every <typeparamref name="T"/> published from now on, on the frame loop's
    /// thread as each frame starts, until the subscription is disposed. A watcher that throws is logged, and the others
    /// still hear the message. What a watcher publishes is heard the frame after.
    /// </summary>
    public Subscription Watch<T>(Action<T> watcher)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(watcher);
        EventQueue<T> queue = GetQueue<T>();
        queue.Add(watcher);

        return new Subscription(() => queue.Remove(watcher));
    }

    /// <summary>
    /// What was published since the last take, as the frame about to start reads it, after handing it to the watchers.
    /// The frame loop's, once a frame.
    /// </summary>
    internal FrameEvents Take()
    {
        // Every type is taken before any watcher hears one, so what a watcher publishes waits for the next take.
        IEventQueue[] all = Volatile.Read(ref _all);
        foreach (IEventQueue queue in all)
        {
            queue.Take();
        }

        foreach (IEventQueue queue in all)
        {
            queue.Deliver();
        }

        return new FrameEvents(this);
    }

    /// <summary>
    /// What the last take took of <typeparamref name="T"/>; empty when none was ever published.
    /// </summary>
    internal ReadOnlySpan<T> GetTaken<T>()
        where T : struct => _queues.TryGetValue(typeof(T), out IEventQueue? queue) ? ((EventQueue<T>)queue).Taken : [];

    private EventQueue<T> GetQueue<T>()
        where T : struct
    {
        if (_queues.TryGetValue(typeof(T), out IEventQueue? found))
            return (EventQueue<T>)found;

        lock (_adding)
        {
            if (_queues.TryGetValue(typeof(T), out found))
                return (EventQueue<T>)found;

            EventQueue<T> added = new();
            _queues[typeof(T)] = added;
            Volatile.Write(ref _all, [.. _all, added]);

            return added;
        }
    }

    /// <summary>
    /// The queues of every type published or watched, as one kind for the take: type erasure over
    /// <see cref="EventQueue{T}"/>.
    /// </summary>
    private interface IEventQueue
    {
        void Take();

        void Deliver();
    }

    /// <summary>
    /// The messages of one type and its watchers.
    /// </summary>
    private sealed class EventQueue<T> : IEventQueue
        where T : struct
    {
        private readonly Lock _changing = new();

        /// <summary>
        /// What the last take took, at its start, reused take after take and grown as needed.
        /// </summary>
        private T[] _taken = [];
        private int _takenCount;

        /// <summary>
        /// Replaced whole as watchers come and go, so delivery walks a set that does not change under it.
        /// </summary>
        private Action<T>[] _watchers = [];

        public ConcurrentQueue<T> Published { get; } = new();

        public ReadOnlySpan<T> Taken => _taken.AsSpan(0, _takenCount);

        public void Add(Action<T> watcher)
        {
            lock (_changing)
            {
                Volatile.Write(ref _watchers, [.. _watchers, watcher]);
            }
        }

        public void Remove(Action<T> watcher)
        {
            lock (_changing)
            {
                int index = Array.IndexOf(_watchers, watcher);
                if (index >= 0)
                    Volatile.Write(ref _watchers, [.. _watchers.AsSpan(0, index), .. _watchers.AsSpan(index + 1)]);
            }
        }

        public void Take()
        {
            // As many as were published when the take began; one published meanwhile waits for the next.
            int count = Published.Count;
            if (_taken.Length < count)
                Array.Resize(ref _taken, (int)BitOperations.RoundUpToPowerOf2((uint)count));

            _takenCount = 0;
            while (_takenCount < count && Published.TryDequeue(out T message))
            {
                _taken[_takenCount++] = message;
            }
        }

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Whatever a watcher throws, it is logged and the others still hear the message."
        )]
        public void Deliver()
        {
            Action<T>[] watchers = Volatile.Read(ref _watchers);
            if (watchers.Length == 0)
                return;

            foreach (T message in Taken)
            {
                foreach (Action<T> watcher in watchers)
                {
                    try
                    {
                        watcher(message);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"A watcher of {typeof(T).Name} threw: {ex}");
                    }
                }
            }
        }
    }
}

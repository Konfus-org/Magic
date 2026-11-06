using System.Collections.Concurrent;
using System.Numerics;
using Magic.Projects;

namespace Magic.Events;

/// <summary>
/// Messages between gems, by type. Anything publishes one, from any thread, and every gem hears it the next frame,
/// read from that frame's <see cref="Frame.Events"/> in a hook. A message is a struct, and publishing and reading
/// allocate nothing once its type's buffers have grown.
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
    /// What was published since the last take, as the frame about to start reads it. The frame loop's, once a frame.
    /// </summary>
    internal FrameEvents Take()
    {
        foreach (IEventQueue queue in Volatile.Read(ref _all))
        {
            queue.Take();
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
    /// The queues of every type published, as one kind for the take: type erasure over <see cref="EventQueue{T}"/>.
    /// </summary>
    private interface IEventQueue
    {
        void Take();
    }

    /// <summary>
    /// The messages of one type.
    /// </summary>
    private sealed class EventQueue<T> : IEventQueue
        where T : struct
    {
        /// <summary>
        /// What the last take took, at its start, reused take after take and grown as needed.
        /// </summary>
        private T[] _taken = [];
        private int _takenCount;

        public ConcurrentQueue<T> Published { get; } = new();

        public ReadOnlySpan<T> Taken => _taken.AsSpan(0, _takenCount);

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
    }
}

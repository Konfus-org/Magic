using System.Collections.Concurrent;
using System.Numerics;
using Magic.Projects;

namespace Magic.Events;

/// <summary>
/// Carries messages between gems, with one queue for each message type.
/// </summary>
/// <remarks>
/// Any thread can publish a message. Each gem reads it in the next frame from <see cref="Frame.Events"/> in a hook, in
/// load order, until a gem handles it. A message is a struct. After the buffers for its type have grown, publish and
/// read do not allocate.
/// </remarks>
public sealed class EventCoordinator
{
    private readonly ConcurrentDictionary<Type, IEventQueue> _queues = new();
    private readonly Lock _adding = new();

    /// <summary>
    /// The queues of <see cref="_queues"/> as an array, replaced when a queue is added, so that a take does not
    /// allocate.
    /// </summary>
    private IEventQueue[] _all = [];

    /// <summary>
    /// Gives <paramref name="message"/> to every gem in the next frame.
    /// </summary>
    /// <remarks>
    /// You can call it from any thread.
    /// </remarks>
    public void Publish<T>(in T message)
        where T : struct, IEvent => GetQueue<T>().Published.Enqueue(message);

    /// <summary>
    /// Takes the messages published since the last take, for the frame that is about to start.
    /// </summary>
    /// <remarks>
    /// Call it only from the frame loop, once each frame.
    /// </remarks>
    internal FrameEvents Take()
    {
        foreach (IEventQueue queue in Volatile.Read(ref _all))
        {
            queue.Take();
        }

        return new FrameEvents(this);
    }

    /// <summary>
    /// Gets the messages of type <typeparamref name="T"/> from the last take that no gem has handled.
    /// </summary>
    /// <remarks>
    /// Call it only on the game thread.
    /// </remarks>
    internal Span<T> GetTaken<T>()
        where T : struct, IEvent =>
        _queues.TryGetValue(typeof(T), out IEventQueue? queue) ? ((EventQueue<T>)queue).GetUnhandled() : [];

    private EventQueue<T> GetQueue<T>()
        where T : struct, IEvent
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
    /// A queue of one message type with its type erased (<see cref="EventQueue{T}"/>), so that a take can go through
    /// all queues.
    /// </summary>
    private interface IEventQueue
    {
        void Take();
    }

    /// <summary>
    /// The messages of one type.
    /// </summary>
    private sealed class EventQueue<T> : IEventQueue
        where T : struct, IEvent
    {
        /// <summary>
        /// The messages of the last take, at the start of an array that each take reuses and grows when needed.
        /// </summary>
        private T[] _taken = [];
        private int _takenCount;

        public ConcurrentQueue<T> Published { get; } = new();

        /// <summary>
        /// Removes the handled messages from the last take and returns the rest, so that a reader does not get a
        /// message that an earlier reader handled.
        /// </summary>
        public Span<T> GetUnhandled()
        {
            int kept = 0;
            for (int i = 0; i < _takenCount; i++)
            {
                if (!_taken[i].IsHandled)
                    _taken[kept++] = _taken[i];
            }

            _takenCount = kept;

            return _taken.AsSpan(0, _takenCount);
        }

        public void Take()
        {
            // Take only the messages published before the take began. A message published during the take waits for the
            // next take.
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

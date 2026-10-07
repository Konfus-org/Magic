using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// The event queues, one per event type: any struct is an event. Publish from any thread; once a frame the frame loop
/// calls <see cref="NextFrame"/>, and from then until the next one <see cref="Of{T}"/> answers everything of a type
/// published before it, in publish order (the frame hands this service down as <see cref="Contexts.Frame.Events"/>).
/// Two ways to read: <see cref="Of{T}"/> in a hook, or <see cref="Watch{T}"/> a type and have each one handed to you.
/// Watchers run on the main thread inside <see cref="NextFrame"/>, before any hook of that frame. Dispose the watch to
/// stop: a gem disposes its watches in its own Dispose, or its old assembly stays alive after a reload. The buffers are
/// kept from frame to frame, so a steady frame allocates nothing.
/// </summary>
public sealed class Events
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, Channel> _channels = [];
    private readonly List<Channel> _published = []; // the channels with something queued, in the order they first got it

    public void Publish<T>(in T e) where T : struct
    {
        lock (_lock)
        {
            Channel<T> channel = ChannelOf<T>();
            if (channel.Queued.Count == 0)
                _published.Add(channel);

            channel.Queued.Add(e);
        }
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with every <typeparamref name="T"/> published, main thread, until disposed. The
    /// watch is taken and disposed on the main thread, or on the render thread while the main one waits on it.
    /// </summary>
    public Subscription Watch<T>(Action<T> handler) where T : struct
    {
        Channel<T> channel;
        lock (_lock)
            channel = ChannelOf<T>();

        channel.Watchers.Add(handler);
        return new Subscription(() => channel.Watchers.Remove(handler));
    }

    /// <summary>
    /// The <typeparamref name="T"/>s of this frame, in publish order. Main thread, or the render thread while the main
    /// one waits on it.
    /// </summary>
    public ReadOnlySpan<T> Of<T>() where T : struct
    {
        lock (_lock)
            return _channels.TryGetValue(typeof(T), out Channel? channel) ? ((Channel<T>)channel).Frame : [];
    }

    /// <summary>
    /// Main thread, once a frame: what was published since the last call becomes this frame's, and the watchers run
    /// over it, channel by channel in the order each first had something.
    /// </summary>
    internal void NextFrame()
    {
        Channel[] published;
        lock (_lock)
        {
            foreach (Channel channel in _channels.Values)
                channel.Take();

            published = [.. _published];
            _published.Clear();
        }

        foreach (Channel channel in published)
            channel.Dispatch();
    }

    private Channel<T> ChannelOf<T>() where T : struct
    {
        if (_channels.TryGetValue(typeof(T), out Channel? channel))
            return (Channel<T>)channel;

        Channel<T> created = new();
        _channels.Add(typeof(T), created);
        return created;
    }

    private abstract class Channel
    {
        /// <summary>
        /// Under the lock: the queued events become the frame's.
        /// </summary>
        public abstract void Take();

        /// <summary>
        /// Hands every event of the frame to every watcher.
        /// </summary>
        public abstract void Dispatch();
    }

    private sealed class Channel<T> : Channel where T : struct
    {
        private T[] _frame = [];
        private int _count;
        private Action<T>[] _calling = [];

        public List<T> Queued { get; } = [];

        public List<Action<T>> Watchers { get; } = [];

        public ReadOnlySpan<T> Frame => _frame.AsSpan(0, _count);

        public override void Take()
        {
            if (_frame.Length < Queued.Count)
                _frame = new T[Math.Max(Queued.Count, _frame.Length * 2)];

            Queued.CopyTo(_frame);
            Array.Clear(_frame, Queued.Count, _count > Queued.Count ? _count - Queued.Count : 0); // let go of old strings
            _count = Queued.Count;
            Queued.Clear();
        }

        public override void Dispatch()
        {
            if (Watchers.Count == 0)
                return;

            // A handler may watch or unwatch: the watchers are copied first, into an array kept for it.
            int watchers = Watchers.Count;
            if (_calling.Length < watchers)
                _calling = new Action<T>[watchers];

            Watchers.CopyTo(_calling);
            for (int i = 0; i < _count; i++)
            {
                for (int w = 0; w < watchers; w++)
                    Call(_calling[w], _frame[i]);
            }

            Array.Clear(_calling, 0, watchers);
        }

        private static void Call(Action<T> handler, T published)
        {
            try
            {
                handler(published);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"A {typeof(T).Name} watcher threw and was skipped. {ex}");
            }
        }
    }
}

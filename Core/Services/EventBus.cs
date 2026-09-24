using Magic.Utils;
using System.Collections.Concurrent;

namespace Magic.Services;

/// <summary>
/// Publish and subscribe by event type, with nothing between the two but the type. An event is a struct;
/// a subscriber gives an <see cref="Action{T}"/> and gets a handle to stop. Publishing is one dictionary
/// lookup and a loop over an array (no allocation), on the publisher's thread: a handler that needs the main
/// thread queues for it itself. A handler that throws is logged and skipped; the others still run. A
/// subscription lives until its handle is disposed: a gem disposes the handles it took when it unloads.
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, object> _channels = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        Channel<T> channel = (Channel<T>)_channels.GetOrAdd(typeof(T), static _ => new Channel<T>());
        channel.Add(handler);
        return new Subscription<T>(channel, handler);
    }

    public void Publish<T>(in T e) where T : struct
    {
        if (_channels.TryGetValue(typeof(T), out object? channel))
            ((Channel<T>)channel).Publish(in e);
    }

    /// <summary>The handlers of one event type. Copy on write, so publishing never locks.</summary>
    private sealed class Channel<T> where T : struct
    {
        private readonly Lock _lock = new();
        private Action<T>[] _handlers = [];

        public void Add(Action<T> handler)
        {
            lock (_lock)
            {
                _handlers = [.. _handlers, handler];
            }
        }

        public void Remove(Action<T> handler)
        {
            lock (_lock)
            {
                int index = Array.IndexOf(_handlers, handler);
                if (index >= 0)
                    _handlers = [.. _handlers[..index], .. _handlers[(index + 1)..]];
            }
        }

        public void Publish(in T e)
        {
            Action<T>[] handlers = Volatile.Read(ref _handlers);
            foreach (Action<T> handler in handlers)
            {
                try
                {
                    handler(e);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Debugging.LogError($"A {typeof(T).Name} handler threw and was skipped. {ex}");
                }
            }
        }
    }

    private sealed class Subscription<T>(Channel<T> channel, Action<T> handler) : IDisposable where T : struct
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                channel.Remove(handler);
        }
    }
}

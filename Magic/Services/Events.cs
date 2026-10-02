using Magic.Contexts.Events;
using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// The event queue. Publish from any thread; once a frame the frame loop takes everything published so far with
/// <see cref="NextFrame"/> and hands it down in <see cref="Contexts.Frame.Events"/>. Two ways to read: loop over
/// <see cref="Contexts.Frame.Events"/> in a hook, or <see cref="Watch"/> a type and have it handed to you. Watchers
/// run on the main thread inside <see cref="NextFrame"/>, in publish order, before any hook of that frame. Dispose
/// the watch to stop: a gem disposes its watches in its own Dispose, or its old assembly stays alive after a reload.
/// </summary>
public sealed class Events
{
    private readonly Lock _lock = new();
    private readonly List<Event> _queued = [];
    private readonly List<Watcher> _watchers = [];

    public void Publish(in Event e)
    {
        lock (_lock)
            _queued.Add(e);
    }

    /// <summary>Calls <paramref name="handler"/> with every event of <paramref name="type"/>, main thread, until disposed.</summary>
    public IDisposable Watch(EventType type, Action<Event> handler)
    {
        Watcher watcher = new(this, type, handler);
        _watchers.Add(watcher);

        return watcher;
    }

    /// <summary>Main thread, once a frame: takes the queue, runs the watchers over it and returns it for the frame.</summary>
    public Event[] NextFrame()
    {
        Event[] events;
        lock (_lock)
        {
            events = [.. _queued];
            _queued.Clear();
        }

        if (_watchers.Count == 0)
            return events;

        foreach (Event published in events)
        {
            foreach (Watcher watcher in _watchers.ToArray()) // a handler may watch or unwatch
            {
                if (watcher.Type == published.Type)
                    Call(watcher, published);
            }
        }

        return events;
    }

    private static void Call(Watcher watcher, Event published)
    {
        try
        {
            watcher.Handler(published);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Error($"A {published.Type} watcher threw and was skipped. {ex}");
        }
    }

    private sealed class Watcher(Events owner, EventType type, Action<Event> handler) : IDisposable
    {
        public EventType Type { get; } = type;

        public Action<Event> Handler { get; } = handler;

        public void Dispose()
        {
            owner._watchers.Remove(this);
        }
    }
}

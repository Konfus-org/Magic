using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;

namespace Magic.Services;

/// <summary>
/// What is open. The world is a stack of <see cref="Domain"/>s: <see cref="Open"/> puts one in, alone or on top of
/// the others, <see cref="Close"/> takes one out, and <see cref="End"/> quits the game. It only decides and says so:
/// each change is published as an event (<see cref="EventType.DomainOpened"/>, <see cref="EventType.DomainClosed"/>,
/// <see cref="EventType.Quit"/>) and whoever acts on it reads it from the next frame's events. It knows nothing of
/// entities, files or streaming. Main thread only.
/// </summary>
public sealed class World(Events events)
{
    private readonly List<Handle<Domain>> _active = [];

    /// <summary>The domains asked to be open, in the order they were opened.</summary>
    public IReadOnlyList<Handle<Domain>> Active => _active;

    /// <summary>
    /// Opens <paramref name="domain"/>: with <see cref="OpenMode.Replace"/> every open domain is closed first, with
    /// <see cref="OpenMode.Additive"/> it joins them (one already open stays as it is). Replacing with
    /// <see cref="Handle{T}.None"/>, the void, leaves an empty world.
    /// </summary>
    public void Open(Handle<Domain> domain, OpenMode mode = OpenMode.Replace)
    {
        if (mode == OpenMode.Replace)
        {
            while (_active.Count > 0)
                Close(_active[^1]);
        }

        if (!domain.IsValid || _active.Contains(domain))
            return;

        _active.Add(domain);
        events.Publish(new Event(EventType.DomainOpened, Id: domain.Id));
    }

    /// <summary>Closes <paramref name="domain"/>; nothing happens when it is not open.</summary>
    public void Close(Handle<Domain> domain)
    {
        if (_active.Remove(domain))
            events.Publish(new Event(EventType.DomainClosed, Id: domain.Id));
    }

    /// <summary>Quits the game: the frame loop stops once the frame that hears of it is done.</summary>
    public void End()
    {
        events.Publish(new Event(EventType.Quit));
    }
}

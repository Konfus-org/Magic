using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Utils;
using System.Numerics;

namespace Magic.Services;

/// <summary>
/// What is open. The world is a stack of <see cref="Domain"/>s: <see cref="Open"/> puts one in, alone or on top of
/// the others, <see cref="Close"/> takes one out, and <see cref="End"/> quits the game. Opening loads the domain's
/// own file, so it answers whether that worked; what is in <see cref="Active"/> is always a domain there is. The
/// streaming system, which reads it every frame, fills it with the domain's chunks and says here how far that is:
/// a domain is <see cref="DomainState.Loading"/> until it is there, which <see cref="StateOf"/> answers and an
/// <see cref="EventType.DomainLoaded"/> event tells. A domain opened in place of the others has the
/// <see cref="Loading"/> domain opened before it, which is closed again once nothing else is loading: what is on
/// screen while the world fills. It knows nothing of entities or streaming. Main thread only, but for
/// <see cref="OpenAsync"/>.
/// </summary>
public sealed class World(Events events, Assets assets, Threads threads)
{
    private readonly List<(Handle<Domain> Domain, DomainState State, float Progress)> _active = [];
    private readonly Dictionary<ulong, IProgress<float>> _progress = []; // by domain id, for those opened with one

    /// <summary>
    /// The open domains, in the order they were opened, each with how far it is: its state, and how much of a
    /// loading one is there, 0 to 1.
    /// </summary>
    public IReadOnlyList<(Handle<Domain> Domain, DomainState State, float Progress)> Active => _active;

    /// <summary>
    /// The domain shown while one opened with <see cref="OpenMode.Replace"/> fills: the project's, until a script
    /// sets another. <see cref="Handle{T}.None"/> shows none, and the opened domain is then drawn as it fills.
    /// </summary>
    public Handle<Domain> Loading { get; set; }

    /// <summary>
    /// Opens <paramref name="domain"/>: with <see cref="OpenMode.Replace"/> every open domain is closed first, with
    /// <see cref="OpenMode.Additive"/> it joins them (one already open stays as it is). Replacing with
    /// <see cref="Handle{T}.None"/>, the void, leaves an empty world. A failure is a domain whose file cannot be
    /// loaded or that is named like one that stays open; the world is then as it was. A domain that replaces the
    /// others is not drawn, and its scripts wait, until it is loaded; <see cref="Loading"/> is shown meanwhile.
    /// The calling thread waits for
    /// the domain's file, which is small; its chunks stream in afterwards, and while they do
    /// <paramref name="progress"/> is told how far that is, 0 to 1, by the streaming system's frame.
    /// </summary>
    public Result Open(Handle<Domain> domain, OpenMode mode = OpenMode.Replace, IProgress<float>? progress = null)
    {
        return Enter(domain, domain.IsValid ? assets.Load(domain) : null, mode, progress);
    }

    /// <summary>
    /// <see cref="Open"/> without a thread waiting for the domain's file: it is read off the calling thread, and the
    /// domain is opened on the main thread once it has arrived. Cancelling <paramref name="cancel"/> before then
    /// throws <see cref="OperationCanceledException"/> and opens nothing. Any thread.
    /// </summary>
    public async Task<Result> OpenAsync(Handle<Domain> domain, OpenMode mode = OpenMode.Replace, IProgress<float>? progress = null, CancellationToken cancel = default)
    {
        Domain? loaded = domain.IsValid ? await assets.LoadAsync(domain, cancel: cancel).ConfigureAwait(false) : null;
        return await threads.InvokeAsync(ThreadId.Main, _ => Enter(domain, loaded, mode, progress), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, with whatever of it is loading; nothing happens when it is not open.
    /// </summary>
    public void Close(Handle<Domain> domain)
    {
        int index = _active.FindIndex(open => open.Domain == domain);
        if (index < 0)
            return;

        _active.RemoveAt(index);
        _progress.Remove(domain.Id);
    }

    /// <summary>
    /// How far <paramref name="domain"/> is; <see cref="DomainState.Closed"/> for one that is not open.
    /// </summary>
    public DomainState StateOf(Handle<Domain> domain)
    {
        foreach ((Handle<Domain> open, DomainState state, _) in _active)
        {
            if (open == domain)
                return state;
        }

        return DomainState.Closed;
    }

    /// <summary>
    /// Spawns <paramref name="chunk"/>'s entities once, under a root placed at <paramref name="at"/> (its own positions
    /// are relative to that), outside any domain: they stay until <see cref="Open"/> replaces the world. The
    /// streaming system does it in its next frame, like a global chunk; a chunk that cannot be read is logged.
    /// </summary>
    public void Spawn(Handle<Chunk> chunk, Vector3 at)
    {
        Spawns.Add((chunk, at));
    }

    /// <summary>
    /// Quits the game: the frame loop stops once the frame that hears of it is done.
    /// </summary>
    public void End()
    {
        events.Publish(new Event(EventType.Quit));
    }

    /// <summary>
    /// The streaming system says how far an open domain is. One that is <see cref="DomainState.Loaded"/> has its
    /// progress told 1 and <see cref="EventType.DomainLoaded"/> published; when it was the last one loading, the
    /// <see cref="Loading"/> domain is closed. Nothing for a domain that is not open.
    /// </summary>
    /// <summary>
    /// What <see cref="Spawn"/> asked for and the streaming system has not taken yet.
    /// </summary>
    public List<(Handle<Chunk> Chunk, Vector3 At)> Spawns { get; } = [];

    public void Set(Handle<Domain> domain, DomainState state)
    {
        int index = _active.FindIndex(open => open.Domain == domain);
        if (index < 0)
            return;

        _active[index] = (domain, state, 0f);
        if (state != DomainState.Loaded)
            return;

        Report(domain, 1f);
        events.Publish(new Event(EventType.DomainLoaded, Id: domain.Id));

        if (!_active.Exists(open => open.Domain != Loading && open.State == DomainState.Loading))
            Close(Loading);
    }

    /// <summary>
    /// The streaming system says how much of a loading domain is there, 0 to 1.
    /// </summary>
    public void Report(Handle<Domain> domain, float progress)
    {
        int index = _active.FindIndex(open => open.Domain == domain);
        if (index >= 0)
            _active[index] = (domain, _active[index].State, progress);

        if (_progress.TryGetValue(domain.Id, out IProgress<float>? told))
            told.Report(progress);
    }

    /// <summary>
    /// What both ways of opening do once the domain's file is read; <paramref name="loaded"/> is null when it could not be.
    /// </summary>
    private Result Enter(Handle<Domain> domain, Domain? loaded, OpenMode mode, IProgress<float>? progress)
    {
        if (domain.IsValid && loaded is null)
            return Result.Failure($"Domain {assets.PathOf(domain.Id) ?? domain.Id.ToString()} cannot be opened: it could not be loaded.");

        if (loaded is not null && mode == OpenMode.Additive && StateOf(domain) == DomainState.Closed)
        {
            foreach ((Handle<Domain> open, _, _) in _active)
            {
                if (assets.Load(open)?.Name == loaded.Name)
                    return Result.Failure($"Domain {loaded.Path} cannot be opened: another open domain is named {loaded.Name}.");
            }
        }

        if (mode == OpenMode.Replace)
        {
            while (_active.Count > 0)
                Close(_active[^1].Domain);
        }

        if (!domain.IsValid || StateOf(domain) != DomainState.Closed)
            return Result.Success();

        // What is shown while it fills; a loading domain that cannot be loaded, or is named like it, is done without.
        if (mode == OpenMode.Replace && loaded is not null && Loading.IsValid && Loading != domain && assets.Load(Loading) is { } screen && screen.Name != loaded.Name)
            _active.Add((Loading, DomainState.Loading, 0f));

        _active.Add((domain, DomainState.Loading, 0f));
        if (progress is not null)
            _progress[domain.Id] = progress;

        return Result.Success();
    }
}

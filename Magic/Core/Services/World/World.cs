using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Domain;
using Magic.Contexts.Events;
using Magic.Contexts.Settings;
using Magic.Contexts.Threading;
using Magic.Utils;
using System.Numerics;

namespace Magic.Services;

/// <summary>
/// The service: what is open. The world is a stack of <see cref="Domain"/>s (<see cref="Domains"/>):
/// <see cref="Open"/> puts one in, in place of the others or beside them, <see cref="Close"/> takes one out, and
/// <see cref="End"/> quits the game. Opening loads the domain's own file, so it answers whether that worked; what is
/// in <see cref="Domains"/> is always a domain there is. Whatever fills the world (the streaming system) reads them
/// every frame and says on each <see cref="OpenDomain"/> how far it is. It knows nothing of entities or streaming.
/// Once the gems are loaded the frame loop has it open the entry point (<see cref="OpenEntryPoint"/>). Main thread only,
/// but for <see cref="OpenAsync"/>.
/// </summary>
public sealed class World
{
    /// <summary>
    /// The entity every domain's root is under: <c>World.&lt;Domain&gt;</c>.
    /// </summary>
    public const string RootName = "World";

    /// <summary>
    /// What was summoned or spawned outside any domain goes under <c>World.Summoned</c>, and with the world.
    /// </summary>
    public const string SummonedName = "Summoned";

    private readonly Events _events;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly List<OpenDomain> _domains = [];

    public World(Events events, Assets assets, Threads threads)
    {
        _events = events;
        _assets = assets;
        _threads = threads;
    }

    /// <summary>
    /// The open domains, in the order they were opened.
    /// </summary>
    public IReadOnlyList<OpenDomain> Domains => _domains;

    /// <summary>
    /// <see cref="DomainState.Loading"/> while a domain opened in place of the world is not there yet,
    /// <see cref="DomainState.Loaded"/> once every one is, <see cref="DomainState.Closed"/> when nothing but overlays is open.
    /// </summary>
    public DomainState State
    {
        get
        {
            DomainState state = DomainState.Closed;
            foreach (OpenDomain open in _domains)
            {
                if (open.Mode == OpenMode.Overlay)
                    continue;

                if (open.Mode == OpenMode.Replace && open.State == DomainState.Loading)
                    return DomainState.Loading;

                state = DomainState.Loaded;
            }

            return state;
        }
    }

    /// <summary>
    /// Opens <paramref name="domain"/> as <paramref name="mode"/> says. Replacing with <see cref="Handle{T}.None"/>, the
    /// void, leaves an empty world. A failure is a domain whose file cannot be loaded or that is named like one that
    /// stays open; the world is then as it was. The calling thread waits for the domain's file, which is small; its
    /// chunks stream in afterwards, and while they do <paramref name="progress"/> is told how far that is, 0 to 1.
    /// </summary>
    public Result Open(Handle<Domain> domain, OpenMode mode = OpenMode.Replace, IProgress<float>? progress = null)
    {
        return Enter(domain, domain.IsValid ? _assets.Load(domain) : null, mode, progress);
    }

    /// <summary>
    /// <see cref="Open"/> without a thread waiting for the domain's file: it is read off the calling thread, and the
    /// domain is opened on the main thread once it has arrived. Cancelling <paramref name="cancel"/> before then
    /// throws <see cref="OperationCanceledException"/> and opens nothing. Any thread.
    /// </summary>
    public async Task<Result> OpenAsync(Handle<Domain> domain, OpenMode mode = OpenMode.Replace, IProgress<float>? progress = null, CancellationToken cancel = default)
    {
        Domain? loaded = domain.IsValid ? await _assets.LoadAsync(domain, cancel: cancel).ConfigureAwait(false) : null;
        return await _threads.InvokeAsync(ThreadId.Main, _ => Enter(domain, loaded, mode, progress), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, with whatever of it is loading; nothing happens when it is not open.
    /// </summary>
    public void Close(Handle<Domain> domain)
    {
        if (Find(domain) is not { } open)
            return;

        _domains.Remove(open);
        open.State = DomainState.Closed;
    }

    /// <summary>
    /// The open <paramref name="domain"/>; null when it is not open.
    /// </summary>
    public OpenDomain? Find(Handle<Domain> domain)
    {
        foreach (OpenDomain open in _domains)
        {
            if (open.Handle == domain)
                return open;
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="act"/> once none of <paramref name="domains"/> is loading any more: now when none is, else
    /// when the last of them is there. One closed in the meantime is not waited for, and when every one of them was,
    /// nothing runs. Main thread.
    /// </summary>
    public void WhenLoaded(IReadOnlyList<Handle<Domain>> domains, Action act)
    {
        if (!AnyIn(domains, DomainState.Loading))
        {
            if (domains.Count == 0 || AnyIn(domains, DomainState.Loaded))
                act();
            return;
        }

        Subscription? watch = null;
#pragma warning disable CA2000 // the watch disposes itself once the domains are there
        watch = _events.Watch<DomainLoaded>(_ =>
        {
            if (AnyIn(domains, DomainState.Loading))
                return;

            watch?.Dispose();
            if (AnyIn(domains, DomainState.Loaded))
                act();
        });
#pragma warning restore CA2000
    }

    /// <summary>
    /// Spawns <paramref name="chunk"/>'s entities once, under a root placed at <paramref name="at"/> (its own positions
    /// are relative to that), outside any domain: they stay until a domain replaces the world. Whatever fills the
    /// world does it in its next frame (<see cref="ChunkSpawn"/>), like a global chunk; one that cannot be read is logged.
    /// </summary>
    public void Spawn(Handle<Chunk> chunk, Vector3 at)
    {
        _events.Publish(new ChunkSpawn(chunk, at));
    }

    /// <summary>
    /// Quits the game: the frame loop stops once the frame that hears of it is done.
    /// </summary>
    public void End()
    {
        _events.Publish(new Quit());
    }

    /// <summary>
    /// Opens the entry point, as the setting (a path, <c>--entry-point</c>) or else the project (<c>"entryPoint": { "id": N }</c>)
    /// names it; the default domain (<c>Domains/Default/Default.domain</c>) when neither names one or it does not open.
    /// Once, after the gems load, so an entry point may be a gem's asset.
    /// </summary>
    internal void OpenEntryPoint(Project project, WorldSettings settings)
    {
        Handle<Domain> entryPoint = settings.EntryPoint.Length > 0 ? _assets.Find<Domain>(settings.EntryPoint) : project.Read<Handle<Domain>>("entryPoint");
        if (settings.EntryPoint.Length > 0 && !entryPoint.IsValid)
            Log.Error($"The entry point {settings.EntryPoint} is no domain among the assets (a gem's or the project's); opening the default domain.");
        else if (!entryPoint.IsValid)
            Log.Info("No entry point is named (\"entryPoint\" in the .magic file, or --entry-point): opening the default domain.");
        else if (Open(entryPoint) is { Failed: true } opened)
            Log.Error($"{opened.Message} Opening the default domain.");
        else
            return;

        if (Open(_assets.Default<Domain>()) is { Failed: true } fallback)
            Log.Error(fallback.Message);
    }

    /// <summary>
    /// What both ways of opening do once the domain's file is read; <paramref name="loaded"/> is null when it could not be.
    /// </summary>
    private Result Enter(Handle<Domain> domain, Domain? loaded, OpenMode mode, IProgress<float>? progress)
    {
        if (domain.IsValid && loaded is null)
            return Result.Failure($"Domain {_assets.PathOf(domain.Id) ?? domain.Id.ToString()} cannot be opened: it could not be loaded.");

        if (mode == OpenMode.Replace)
        {
            for (int i = _domains.Count - 1; i >= 0; i--)
            {
                if (_domains[i].Mode != OpenMode.Overlay)
                    Close(_domains[i].Handle);
            }
        }

        if (loaded is null || Find(domain) is not null)
            return Result.Success();

        foreach (OpenDomain open in _domains)
        {
            if (open.Domain.Name == loaded.Name)
                return Result.Failure($"Domain {loaded.Path} cannot be opened: another open domain is named {loaded.Name}.");
        }

        _domains.Add(new OpenDomain(domain, loaded, mode, progress, _events));
        return Result.Success();
    }

    private bool AnyIn(IReadOnlyList<Handle<Domain>> domains, DomainState state)
    {
        foreach (Handle<Domain> domain in domains)
        {
            if ((Find(domain)?.State ?? DomainState.Closed) == state)
                return true;
        }

        return false;
    }
}

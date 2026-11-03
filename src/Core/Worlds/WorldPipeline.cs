using System.Collections.Immutable;
using System.Linq;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Pipelines;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// The world's side of a run: how content comes and goes, and what the world does on its own. It opens the entry point,
/// without waiting for it, as frames must run while it loads and <see cref="Startup"/> holds them back until it ends, and
/// changes for what the assets say changed.
/// </summary>
internal sealed class WorldPipeline(
    World world,
    AssetDatabase assets,
    Threads threads,
    Project project,
    PipelineCoordinator pipelines
)
    : IPipeline<Startup>,
        IPipeline<AssetsChangeList>,
        IPipeline<WorldOpen>,
        IPipeline<WorldLoad>,
        IPipeline<WorldUnload>,
        IPipeline<WorldRestore>,
        IPipeline<WorldSpawn<Prefab>>
{
    /// <summary>
    /// The content of what no content placed, among a saved world's entities.
    /// </summary>
    private const ulong NoContent = 0;

    private readonly PipelineRunner<EntitiesChangeList> _entities = pipelines.Get<EntitiesChangeList>();

    public ValueTask<Startup> RunAsync(Startup state, CancellationToken cancel)
    {
        OpenEntryPointAsync(cancel).FireAndForget();

        return ValueTask.FromResult(state);
    }

    public ValueTask<AssetsChangeList> RunAsync(AssetsChangeList state, CancellationToken cancel)
    {
        foreach (AssetLoaded reloaded in state.Reloaded)
        {
            if (reloaded.Asset is Prefab prefab)
                ReloadPrefabAsync(new Handle<Prefab>(reloaded.Id), prefab, cancel).FireAndForget();
            else if (reloaded.Asset is Domain domain)
                ApplyDomainAsync(new Handle<Domain>(reloaded.Id), domain, cancel).FireAndForget();
        }

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Reads the domain on workers, so one that doesn't load leaves the world as it was. One already open beside the others
    /// is just found. A replace shows the loading screen and clears the world around bringing it in.
    /// </summary>
    public async ValueTask<WorldOpen> RunAsync(WorldOpen state, CancellationToken cancel)
    {
        if (!world.HasEcs)
            return state with { Error = "no gem the project lists provides the ECS." };

        bool isReplace = state.Mode == DomainOpenMode.Replace;
        if (!isReplace && world.FindOpen(state.Domain) is { } open)
            return state with { Content = open };

        Handle<Prefab> player = isReplace ? project.Player : Handle<Prefab>.None;
        Result<ImmutableArray<EntityTemplate>> read = await ReadDomainAsync(
                state.Content,
                player,
                state.Progress,
                cancel
            )
            .ConfigureAwait(false);
        if (!read.Ok)
            return state with { Error = read.Error };

        WorldContent? loading = null;
        if (isReplace)
        {
            world.IsLoading = true;
            loading = await ShowLoadingScreenAsync(cancel).ConfigureAwait(false);
        }

        await threads.Enter(EngineThread.Main);
        if (isReplace)
            await ClearAsync(state.Progress, cancel).ConfigureAwait(false);

        await world.AddAsync(state.Content, read.Value, state.Progress, cancel).ConfigureAwait(false);
        if (loading is { } shown)
            await world.LeaveAsync(shown, WorldEntities.FindOf(world, shown.Id), null, cancel).ConfigureAwait(false);

        return state with
        {
            Coming = read.Value,
        };
    }

    /// <summary>
    /// Makes the entities of what comes, first, so every gem's part has them in <see cref="WorldLoad.Arrived"/>.
    /// </summary>
    public ValueTask<WorldLoad> RunAsync(WorldLoad state, CancellationToken cancel) =>
        ValueTask.FromResult(state with { Arrived = MakeEntities(state.Coming, cancel) });

    /// <summary>
    /// Destroys the entities of what leaves. <see cref="WorldUnload"/> runs last first, so this, Core's, runs once every gem
    /// has let go of them.
    /// </summary>
    public ValueTask<WorldUnload> RunAsync(WorldUnload state, CancellationToken cancel)
    {
        DestroyEntities(state.Leaving, cancel);

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Everything leaves, globals too, and what the saved world holds comes: each content with its own entities, then those
    /// made as the game ran.
    /// </summary>
    public async ValueTask<WorldRestore> RunAsync(WorldRestore state, CancellationToken cancel)
    {
        if (!world.HasEcs)
        {
            Log.Warn("The world is not restored: no gem the project lists provides the ECS.");

            return state;
        }

        world.IsLoading = true;
        foreach (WorldContent content in world.Contents)
        {
            await world.RemoveAsync(content, state.Progress, cancel).ConfigureAwait(false);
        }

        DestroyEntities(WorldEntities.FindAll(world), cancel);

        WorldState saved = state.State;
        Dictionary<ulong, ImmutableArray<EntityTemplate>.Builder> byContent = GroupByContent(saved);
        foreach (WorldContent content in saved.Contents)
        {
            world.KeepContentIdsPast(content.Id);
            await world
                .ComeAsync(content, GetTemplates(byContent, content.Id), state.Progress, cancel)
                .ConfigureAwait(false);
        }

        MakeEntities(GetTemplates(byContent, NoContent), cancel);
        world.Contents = saved.Contents;
        world.IsPaused = saved.IsPaused;
        world.TimeScale = saved.TimeScale;
        Log.Info($"Restored the world: {saved.Entities.Length} entities in {saved.Contents.Length} contents.");

        return state;
    }

    /// <summary>
    /// A prefab's parts make the entities it spawns as: one for each part, a part before the parts under it, its transform
    /// placed under its parent's, and the top parts' under where it is put.
    /// </summary>
    public ValueTask<WorldSpawn<Prefab>> RunAsync(WorldSpawn<Prefab> state, CancellationToken cancel)
    {
        ImmutableArray<EntityTemplate>.Builder entities = ImmutableArray.CreateBuilder<EntityTemplate>();
        AddEntities(state.Asset.Parts, state.At, entities);

        return ValueTask.FromResult(state with { Entities = entities.ToImmutable() });
    }

    private static void AddEntities(
        ImmutableArray<PrefabPart> parts,
        Transform parent,
        ImmutableArray<EntityTemplate>.Builder entities
    )
    {
        foreach (PrefabPart part in parts)
        {
            Transform placed = PlaceUnder(part, parent);
            ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>(
                part.Components.Length
            );
            foreach (IComponent component in part.Components)
            {
                components.Add(component is Transform ? placed : component);
            }

            entities.Add(new EntityTemplate(components.MoveToImmutable()));
            AddEntities(part.Children, placed, entities);
        }
    }

    /// <summary>
    /// Where <paramref name="part"/> is under <paramref name="parent"/>: its transform placed there, or the parent's
    /// place when it has none.
    /// </summary>
    private static Transform PlaceUnder(PrefabPart part, Transform parent)
    {
        foreach (IComponent component in part.Components)
        {
            if (component is Transform local)
                return local.Under(parent);
        }

        return parent;
    }

    /// <summary>
    /// Where a player goes: the first of <paramref name="entities"/> tagged spawn, so a domain's globals before its chunks;
    /// else the origin, which is warned about.
    /// </summary>
    private static Transform FindSpawn(string path, ImmutableArray<EntityTemplate>.Builder entities)
    {
        foreach (EntityTemplate entity in entities)
        {
            bool isSpawn = false;
            Transform? place = null;
            foreach (IComponent component in entity.Components)
            {
                if (component is Tags tags)
                    isSpawn = tags.Has(Tag.Spawn);
                else if (component is Transform transform)
                    place = transform;
            }

            if (isSpawn && place is { } spawn)
                return new Transform { Position = spawn.Position, Rotation = spawn.Rotation };
        }

        Log.Warn($"{path} has no entity tagged spawn, so the player is put at its origin.");

        return new Transform();
    }

    private static bool IsAt(Origin origin, DomainPlace place) =>
        origin.Prefab == place.Prefab && origin.At == place.At;

    private static Dictionary<ulong, ImmutableArray<EntityTemplate>.Builder> GroupByContent(WorldState state)
    {
        Dictionary<ulong, ImmutableArray<EntityTemplate>.Builder> byContent = [];
        foreach (EntityTemplate entity in state.Entities)
        {
            ulong id = NoContent;
            foreach (IComponent component in entity.Components)
            {
                if (component is Origin origin)
                    id = origin.Content;
            }

            if (!byContent.TryGetValue(id, out ImmutableArray<EntityTemplate>.Builder? templates))
            {
                templates = ImmutableArray.CreateBuilder<EntityTemplate>();
                byContent.Add(id, templates);
            }

            templates.Add(entity);
        }

        return byContent;
    }

    private static ImmutableArray<EntityTemplate> GetTemplates(
        Dictionary<ulong, ImmutableArray<EntityTemplate>.Builder> byContent,
        ulong id
    ) =>
        byContent.TryGetValue(id, out ImmutableArray<EntityTemplate>.Builder? templates) ? templates.ToImmutable() : [];

    private ImmutableArray<Entity> MakeEntities(ImmutableArray<EntityTemplate> entities, CancellationToken cancel)
    {
        EntitiesChangeList changes = _entities.Rent();
        changes.Create(entities.AsSpan());
        _entities.Run(changes, cancel);
        ImmutableArray<Entity> made = [.. changes.Created];
        _entities.Return(changes);

        return made;
    }

    private void DestroyEntities(ImmutableArray<Entity> entities, CancellationToken cancel)
    {
        EntitiesChangeList changes = _entities.Rent();
        changes.Destroy(entities.AsSpan());
        _entities.Run(changes, cancel);
        _entities.Return(changes);
    }

    /// <summary>
    /// Opens the project's entry point in place of the world, or the default one when it names none or that doesn't
    /// open. With no ECS, nothing opens, which is warned about.
    /// </summary>
    private async Task OpenEntryPointAsync(CancellationToken cancel)
    {
        if (!world.HasEcs)
        {
            Log.Warn("No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");

            return;
        }

        Handle<Domain> fallback = assets.GetDefault<Domain>();
        Handle<Domain> first = project.EntryPoint == Handle<Domain>.None ? fallback : project.EntryPoint;
        Result<WorldContent> opened = await world
            .OpenAsync(first, DomainOpenMode.Replace, null, cancel)
            .ConfigureAwait(false);
        if (opened.Ok)
            return;

        Log.Error(opened.Error);
        if (first == fallback)
            return;

        Result<WorldContent> second = await world
            .OpenAsync(fallback, DomainOpenMode.Replace, null, cancel)
            .ConfigureAwait(false);
        if (!second.Ok)
            Log.Error($"The default domain does not open either: {second.Error}");
    }

    /// <summary>
    /// A prefab was read again: wherever its entities are, they leave and it comes again there as it is now.
    /// </summary>
    private Task ReloadPrefabAsync(Handle<Prefab> handle, Prefab prefab, CancellationToken cancel) =>
        world.ChangeAsync(
            async () =>
            {
                foreach (Origin origin in WorldEntities.FindOrigins(world, handle))
                {
                    WorldContent content = new(origin.Content, origin.Domain);
                    await world
                        .LeaveAsync(content, WorldEntities.FindOf(world, origin), null, cancel)
                        .ConfigureAwait(false);
                    await ComeAsync(content, prefab, origin.At, handle, cancel).ConfigureAwait(false);
                }
            },
            cancel
        );

    /// <summary>
    /// A domain was read again: if it is open, what it no longer places where it was leaves, and what it now places
    /// where nothing was comes.
    /// </summary>
    private Task ApplyDomainAsync(Handle<Domain> handle, Domain domain, CancellationToken cancel) =>
        world.ChangeAsync(
            async () =>
            {
                if (world.FindOpen(handle) is not { } content)
                    return;

                ImmutableArray<DomainPlace> places = WorldEntities.GetPlaces(domain);
                ImmutableArray<Origin> origins = WorldEntities.FindOrigins(world, content);
                foreach (Origin origin in origins)
                {
                    if (!places.Any(place => IsAt(origin, place)))
                    {
                        await world
                            .LeaveAsync(content, WorldEntities.FindOf(world, origin), null, cancel)
                            .ConfigureAwait(false);
                    }
                }

                foreach (DomainPlace place in places)
                {
                    if (!origins.Any(origin => IsAt(origin, place)))
                        await PlaceAsync(content, place, cancel).ConfigureAwait(false);
                }
            },
            cancel
        );

    /// <summary>
    /// Brings the prefab <paramref name="place"/> names into <paramref name="content"/>; one that doesn't load is an error,
    /// and is tried again the next time the domain is read.
    /// </summary>
    private async Task PlaceAsync(WorldContent content, DomainPlace place, CancellationToken cancel)
    {
        Result<Prefab> prefab = await assets.LoadAsync(place.Prefab, null, cancel).ConfigureAwait(false);
        if (!prefab.Ok)
        {
            Log.Error($"{assets.GetPath(content.Domain)} goes without a prefab: {prefab.Error}");

            return;
        }

        await ComeAsync(content, prefab.Value, place.At, place.Prefab, cancel).ConfigureAwait(false);
    }

    private async Task ComeAsync(
        WorldContent content,
        Prefab prefab,
        Transform at,
        Handle<Prefab> handle,
        CancellationToken cancel
    )
    {
        ImmutableArray<EntityTemplate> coming = await world
            .GetEntitiesAsync(prefab, at, handle, content, cancel)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        await world.ComeAsync(content, coming, null, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The entities of the domain <paramref name="content"/> names, as the pipeline over <see cref="WorldSpawn{T}"/> makes
    /// each prefab it places into them, with the <paramref name="player"/> when there is one at the first entity tagged
    /// spawn. A prefab that doesn't load is an error, and it is left out; why not, when the domain doesn't load or no gem
    /// reads prefabs.
    /// </summary>
    private async Task<Result<ImmutableArray<EntityTemplate>>> ReadDomainAsync(
        WorldContent content,
        Handle<Prefab> player,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<Domain> domain = await assets.LoadAsync(content.Domain, progress, cancel).ConfigureAwait(false);
        if (!domain.Ok)
            return Result.Failure(domain.Error);

        string path = assets.GetPath(content.Domain) ?? "A domain";
        ImmutableArray<DomainPlace> places = WorldEntities.GetPlaces(domain.Value);
        if (places.Length > 0 && !assets.CanRead<Prefab>())
            return Result.Failure($"{path} holds prefabs, and no gem the project lists reads them.");

        Result<Prefab>[] prefabs = await LoadPrefabsAsync(places, progress, cancel).ConfigureAwait(false);
        ImmutableArray<EntityTemplate>.Builder entities = ImmutableArray.CreateBuilder<EntityTemplate>();
        for (int i = 0; i < places.Length; i++)
        {
            if (prefabs[i] is { Ok: true, Value: { } prefab })
            {
                entities.AddRange(
                    await world
                        .GetEntitiesAsync(prefab, places[i].At, places[i].Prefab, content, cancel)
                        .ConfigureAwait(false)
                );
            }
            else
            {
                Log.Error($"{path} goes without a prefab: {prefabs[i].Error}");
            }
        }

        if (player == Handle<Prefab>.None)
            return Result.Success(entities.ToImmutable());

        Result<Prefab> loaded = await assets.LoadAsync(player, progress, cancel).ConfigureAwait(false);
        if (loaded.Ok)
        {
            entities.AddRange(
                await world
                    .GetEntitiesAsync(loaded.Value, FindSpawn(path, entities), Handle<Prefab>.None, content, cancel)
                    .ConfigureAwait(false)
            );
        }
        else
        {
            Log.Error($"The project's player is not spawned: {loaded.Error}");
        }

        return Result.Success(entities.ToImmutable());
    }

    /// <summary>
    /// The prefabs of <paramref name="places"/>, loaded at once, telling how far that is, at the same indexes.
    /// </summary>
    private async Task<Result<Prefab>[]> LoadPrefabsAsync(
        ImmutableArray<DomainPlace> places,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        const string Work = "Reading prefabs";

        int done = 0;
        progress?.Report(new LoadProgress(Work, 0, places.Length));
        Task<Result<Prefab>>[] loading = new Task<Result<Prefab>>[places.Length];
        for (int i = 0; i < loading.Length; i++)
        {
            loading[i] = LoadOneAsync(places[i].Prefab);
        }

        return await Task.WhenAll(loading).ConfigureAwait(false);

        async Task<Result<Prefab>> LoadOneAsync(Handle<Prefab> prefab)
        {
            Result<Prefab> one = await assets.LoadAsync(prefab, null, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(Work, Interlocked.Increment(ref done), places.Length));

            return one;
        }
    }

    /// <summary>
    /// Brings in the project's loading screen, else the engine's: what it is in the world; none, which is warned about, when
    /// it doesn't load.
    /// </summary>
    private async Task<WorldContent?> ShowLoadingScreenAsync(CancellationToken cancel)
    {
        Handle<Domain> domain =
            project.LoadingScreen == Handle<Domain>.None
                ? assets.Find<Domain>("Domains/Loading/Loading.domain")
                : project.LoadingScreen;
        if (domain == Handle<Domain>.None)
            return null;

        WorldContent loading = new(world.GetNextContentId(), domain);
        Result<ImmutableArray<EntityTemplate>> read = await ReadDomainAsync(loading, Handle<Prefab>.None, null, cancel)
            .ConfigureAwait(false);
        if (!read.Ok)
        {
            Log.Warn($"No loading screen is shown: {read.Error}");

            return null;
        }

        await threads.Enter(EngineThread.Main);
        await world.ComeAsync(loading, read.Value, null, cancel).ConfigureAwait(false);

        return loading;
    }

    /// <summary>
    /// Everything in the world leaves but its globals and the loading screen.
    /// </summary>
    private async Task ClearAsync(IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        foreach (WorldContent content in world.Contents)
        {
            await world.RemoveAsync(content, progress, cancel).ConfigureAwait(false);
        }

        DestroyEntities(WorldEntities.FindRuntime(world), cancel);
    }
}

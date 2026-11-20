using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;

namespace Magic.PrefabImporter;

/// <summary>
/// Reads <c>.prefab</c> files into <see cref="Prefab"/>s, with each entity as a part.
/// </summary>
/// <remarks>
/// A part has its components, read as their types by name, its tags as a <see cref="Tags"/>, and the entities under
/// it, which include a prefab that it places. A file is made from the prefabs that load as it is read, so it is read
/// again when one of them changes.
/// </remarks>
public sealed class Importer : IAssetImporter<Prefab>
{
    /// <summary>
    /// A file with at least this many top entities reads them in parallel.
    /// </summary>
    private const int ParallelFrom = 64;

    private const int ProgressSteps = 20;

    private readonly AssetManager _assets;

    internal Importer(AssetManager assets) => _assets = assets;

    public ImmutableArray<string> Extensions { get; } = [".prefab"];

    public int Version => 1;

    public async Task<Result<Prefab>> ImportAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<PrefabFile> read = Json.Read<PrefabFile>(file.Bytes.Span);
        if (!read.Ok)
            return Result.Failure(read.Error);

        // Each prefab placed is loaded once, however many entities place it, and all of them load together.
        Dictionary<ulong, Prefab> placed = [];
        Result loadedAll = await LoadPlacedAsync(read.Value.Entities, file, placed, progress, cancel)
            .ConfigureAwait(false);
        if (!loadedAll.Ok)
            return Result.Failure(loadedAll.Error);

        // Telling how far the file's own top entities are, in coarse steps; a prefab one places tells its own.
        string work = $"Reading {file.Path}";
        ImmutableArray<PrefabFileEntity> entities = read.Value.Entities;
        int step = Math.Max(1, entities.Length / ProgressSteps);
        progress?.Report(new LoadProgress(work, 0, entities.Length));
        PrefabPart[] parts = new PrefabPart[entities.Length];
        string?[] errors = new string?[entities.Length];
        int done = 0;
        void ReadAt(int index)
        {
            Result<PrefabPart> part = ReadPart(entities[index], file, placed);
            if (part.Ok)
                parts[index] = part.Value;
            else
                errors[index] = part.Error;

            int count = Interlocked.Increment(ref done);
            if (count % step == 0)
                progress?.Report(new LoadProgress(work, count, entities.Length));
        }

        if (entities.Length >= ParallelFrom)
            Parallel.For(0, entities.Length, new ParallelOptions { CancellationToken = cancel }, ReadAt);
        else
            for (int index = 0; index < entities.Length; index++)
            {
                ReadAt(index);
            }

        // The first failure in the file's order, so the same file fails the same way.
        foreach (string? error in errors)
        {
            if (error is not null)
                return Result.Failure(error);
        }

        progress?.Report(new LoadProgress(work, entities.Length, entities.Length));

        return Result.Success(new Prefab { Parts = [.. parts] });
    }

    private static void CollectPlaced(
        ImmutableArray<PrefabFileEntity> entities,
        Dictionary<ulong, Handle<Prefab>> handles
    )
    {
        foreach (PrefabFileEntity entity in entities)
        {
            if (entity.Prefab != Handle<Prefab>.None)
                handles.TryAdd(entity.Prefab.Id, entity.Prefab);

            CollectPlaced(entity.Children, handles);
        }
    }

    private static string PlacerName(ImmutableArray<PrefabFileEntity> entities, ulong id)
    {
        foreach (PrefabFileEntity entity in entities)
        {
            if (entity.Prefab.Id == id)
                return entity.Name.Length > 0 ? entity.Name : "an entity";

            string name = PlacerName(entity.Children, id);
            if (name.Length > 0)
                return name;
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads the part that <paramref name="entity"/> is: its components, its tags, and the parts under it.
    /// </summary>
    /// <remarks>
    /// The prefabs that it places are loaded, in <paramref name="placed"/>, and their parts come first. A component
    /// that no code declares, or whose values do not fit it, logs an error, and the part goes without it. A placed
    /// prefab that does not load fails the file.
    /// </remarks>
    private static Result<PrefabPart> ReadPart(
        PrefabFileEntity entity,
        AssetFile file,
        Dictionary<ulong, Prefab> placed
    )
    {
        string name = entity.Name.Length > 0 ? entity.Name : "an entity";
        ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>();
        foreach ((string component, JsonElement json) in entity.Components)
        {
            Result<Type> type = Assemblies.Get<IComponent>(component);
            Result<object> read = type.Ok ? Json.Read(json, type.Value) : Result.Failure(type.Error);
            if (read is { Ok: true, Value: IComponent made })
                components.Add(made);
            else
                Log.Error($"{file.Path}'s {name} goes without component {component}: {read.Error}");
        }

        // Its tags, as one component; an empty name or one past the capacity is left out.
        if (!entity.Tags.IsEmpty)
        {
            Tags tags = default;
            foreach (string tag in entity.Tags.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(tag) || !tags.Add(Tag.From(tag)))
                    Log.Warn(
                        $"{file.Path}'s {name} is tagged \"{tag}\", which is left out: a tag needs a name, and an entity "
                            + $"has at most {Tags.Capacity}."
                    );
            }

            components.Add(tags);
        }

        ImmutableArray<PrefabPart>.Builder children = ImmutableArray.CreateBuilder<PrefabPart>();
        if (entity.Prefab != Handle<Prefab>.None)
        {
            Prefab prefab = placed[entity.Prefab.Id];
            children.AddRange(prefab.Parts);
        }

        foreach (PrefabFileEntity child in entity.Children)
        {
            Result<PrefabPart> part = ReadPart(child, file, placed);
            if (!part.Ok)
                return Result.Failure(part.Error);

            children.Add(part.Value);
        }

        return Result.Success(
            new PrefabPart
            {
                Name = entity.Name,
                Components = components.ToImmutable(),
                Placed = entity.Prefab,
                Children = children.ToImmutable(),
            }
        );
    }

    private async Task<Result> LoadPlacedAsync(
        ImmutableArray<PrefabFileEntity> entities,
        AssetFile file,
        Dictionary<ulong, Prefab> placed,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Dictionary<ulong, Handle<Prefab>> handles = [];
        CollectPlaced(entities, handles);
        if (handles.Count == 0)
            return Result.Success();

        Result<Prefab>[] loaded = await Task.WhenAll(
                handles.Values.Select(handle => _assets.LoadAsync(handle, file.Id, progress, cancel))
            )
            .ConfigureAwait(false);

        // A failure is told by the first entity that places it, as it was when each loaded in turn.
        int at = 0;
        foreach (ulong id in handles.Keys)
        {
            Result<Prefab> result = loaded[at++];
            if (!result.Ok)
                return Result.Failure($"{PlacerName(entities, id)} places a prefab that does not load: {result.Error}");

            placed.Add(id, result.Value);
        }

        return Result.Success();
    }
}

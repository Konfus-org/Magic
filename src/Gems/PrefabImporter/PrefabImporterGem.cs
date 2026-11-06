using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Utils;

namespace Magic.PrefabImporter;

/// <summary>
/// Reads <c>.prefab</c> files into <see cref="Prefab"/>s: each entity a part, with its components read as their types by
/// name, its tags as a <see cref="Tags"/>, and the entities under it, a prefab it places among them. What it loads as it
/// reads one is what that one is made from, so it is read again when one of them changes. It is public because nothing in
/// its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class PrefabImporterGem(Assets assets) : IGem, IAssetImporter<Prefab>
{
    /// <summary>
    /// The components files name that could not be read, each said once. Read on workers.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _unread = new(StringComparer.Ordinal);

    public ImmutableArray<string> Extensions => [".prefab"];

    public async Task<Result<Prefab>> ImportAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<PrefabFile> read = Json.Read<PrefabFile>(file.Bytes.Span);
        if (!read.Ok)
            return Result.Failure(read.Error);

        // Telling how far the file's own top entities are; a prefab one places tells its own.
        string work = $"Reading {file.Path}";
        ImmutableArray<PrefabFileEntity> entities = read.Value.Entities;
        progress?.Report(new LoadProgress(work, 0, entities.Length));
        ImmutableArray<PrefabPart>.Builder parts = ImmutableArray.CreateBuilder<PrefabPart>(entities.Length);

        // Each prefab placed is asked for once, as an ask waits for the assets to take it in, between frames.
        Dictionary<ulong, Prefab> placed = [];
        foreach (PrefabFileEntity entity in entities)
        {
            Result<PrefabPart> part = await ReadPartAsync(entity, file, placed, progress, cancel).ConfigureAwait(false);
            if (!part.Ok)
                return Result.Failure(part.Error);

            parts.Add(part.Value);
            progress?.Report(new LoadProgress(work, parts.Count, entities.Length));
        }

        return Result.Success(new Prefab { Parts = parts.MoveToImmutable() });
    }

    /// <summary>
    /// The entity's tags, or none when it has none. Past <see cref="Tags.Capacity"/>, and an empty name, are warned about
    /// and left out.
    /// </summary>
    private static Tags? ParseTags(PrefabFileEntity entity, string name, string path)
    {
        if (entity.Tags.IsEmpty)
            return null;

        Tags tags = default;
        foreach (string tag in entity.Tags)
        {
            // Adding fails for a tag held already, which is no fault, as well as for an empty one or one past the capacity.
            Tag named = string.IsNullOrEmpty(tag) ? default : Tag.From(tag);
            if (!tags.Add(named) && !tags.Has(named))
                Log.Warn(
                    $"{path}'s {name} is tagged \"{tag}\", which is left out: a tag needs a name, and an entity has at "
                        + $"most {Tags.Capacity}."
                );
        }

        return tags;
    }

    /// <summary>
    /// The part <paramref name="entity"/> is: its components, its tags among them, and the parts under it, those of the
    /// prefab it places first. A component no code declares, or whose values aren't its, is an error logged once, and the
    /// part goes without it; a prefab it places that doesn't load fails the file.
    /// </summary>
    private async Task<Result<PrefabPart>> ReadPartAsync(
        PrefabFileEntity entity,
        AssetFile file,
        Dictionary<ulong, Prefab> placed,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
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
            else if (_unread.TryAdd(component, true))
                Log.Error($"{file.Path}'s {name} goes without component {component}: {read.Error}");
        }

        if (ParseTags(entity, name, file.Path) is { } tags)
            components.Add(tags);

        ImmutableArray<PrefabPart>.Builder children = ImmutableArray.CreateBuilder<PrefabPart>();
        if (entity.Prefab != Handle<Prefab>.None)
        {
            if (!placed.TryGetValue(entity.Prefab.Id, out Prefab? prefab))
            {
                Result<Prefab> loaded = await assets
                    .LoadAsync(entity.Prefab, file.Id, progress, cancel)
                    .ConfigureAwait(false);
                if (!loaded.Ok)
                    return Result.Failure($"{name} places a prefab that does not load: {loaded.Error}");

                prefab = loaded.Value;
                placed.Add(entity.Prefab.Id, prefab);
            }

            children.AddRange(prefab.Parts);
        }

        foreach (PrefabFileEntity child in entity.Children)
        {
            Result<PrefabPart> part = await ReadPartAsync(child, file, placed, progress, cancel).ConfigureAwait(false);
            if (!part.Ok)
                return Result.Failure(part.Error);

            children.Add(part.Value);
        }

        return Result.Success(
            new PrefabPart
            {
                Name = entity.Name,
                Components = components.ToImmutable(),
                Children = children.ToImmutable(),
            }
        );
    }
}

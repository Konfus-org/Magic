using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Gems;
using Magic.Serialization;
using Magic.Utils;
using Magic.Worlds;

namespace WorldLoader;

/// <summary>
/// Reads and writes <c>.world</c> files into <see cref="WorldState"/>s. A file names a component by its type's name and holds
/// its values as JSON, so it outlives the code that made it. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class WorldLoaderGem(Assemblies code) : IGem, IAssetLoader<WorldState>, IAssetWriter<WorldState>
{
    public ImmutableArray<string> Extensions => [".world"];

    public string Extension => ".world";

    /// <summary>
    /// A component no code declares, or whose values no longer fit it, is an error, logged once per type, and the entity goes
    /// without it.
    /// </summary>
    public Task<Result<WorldState>> LoadAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Reading {file.Path}";
        progress?.Report(new LoadProgress(work, 0, 1));
        Result<SavedWorld> saved = Json.Read<SavedWorld>(file.Bytes.Span);
        if (!saved.Ok)
            return Task.FromResult<Result<WorldState>>(Result.Failure(saved.Error));

        WorldState state = ToState(saved.Value);
        progress?.Report(new LoadProgress(work, 1, 1));

        return Task.FromResult(Result.Success(state));
    }

    public async Task<Result<ReadOnlyMemory<byte>>> WriteAsync(
        WorldState asset,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        ArgumentNullException.ThrowIfNull(asset);
        Result<JsonElement> json = await Task.Run(() => ToJson(asset, progress, cancel), cancel).ConfigureAwait(false);

        return json.Ok
            ? Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(json.Value.GetRawText()))
            : Result.Failure(json.Error);
    }

    private static Result<JsonElement> ToJson(
        WorldState state,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        const string Work = "Writing the world";

        HashSet<string> failed = new(StringComparer.Ordinal);
        ImmutableArray<ImmutableArray<SavedComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<SavedComponent>
        >(state.Entities.Length);
        foreach (EntityTemplate entity in state.Entities)
        {
            cancel.ThrowIfCancellationRequested();
            ImmutableArray<SavedComponent>.Builder components = ImmutableArray.CreateBuilder<SavedComponent>();
            foreach (IComponent component in entity.Components)
            {
                Type type = component.GetType();
                Result<JsonElement> values = Json.Write(component, type);
                if (values.Ok)
                    components.Add(new SavedComponent(type.Name, values.Value));
                else if (failed.Add(type.Name))
                    Log.Error($"{type.Name} components are not saved, as JSON cannot hold them: {values.Error}");
            }

            entities.Add(components.ToImmutable());
            progress?.Report(new LoadProgress(Work, entities.Count, state.Entities.Length));
        }

        SavedWorld saved = new(state.Contents, entities.MoveToImmutable(), state.IsPaused, state.TimeScale);

        return Json.Write(saved, typeof(SavedWorld));
    }

    private WorldState ToState(SavedWorld saved)
    {
        HashSet<string> failed = new(StringComparer.Ordinal);
        ImmutableArray<EntityTemplate>.Builder entities = ImmutableArray.CreateBuilder<EntityTemplate>(
            saved.Entities.Length
        );
        foreach (ImmutableArray<SavedComponent> entity in saved.Entities)
        {
            ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>(entity.Length);
            foreach (SavedComponent component in entity)
            {
                Result<Type> type = code.Get<IComponent>(component.Type);
                Result<object> read = type.Ok ? Json.Read(component.Values, type.Value) : Result.Failure(type.Error);
                if (read is { Ok: true, Value: IComponent made })
                    components.Add(made);
                else if (failed.Add(component.Type))
                    Log.Error($"Saved {component.Type} components are not put back: {read.Error}");
            }

            entities.Add(new EntityTemplate(components.ToImmutable()));
        }

        return new WorldState
        {
            Contents = saved.Contents,
            Entities = entities.MoveToImmutable(),
            IsPaused = saved.IsPaused,
            TimeScale = saved.TimeScale,
        };
    }
}

/// <summary>
/// A <c>.world</c> file as it is written.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it as it reads a file."
)]
internal sealed record SavedWorld(
    ImmutableArray<WorldContent> Contents,
    ImmutableArray<ImmutableArray<SavedComponent>> Entities,
    bool IsPaused,
    float TimeScale
);

/// <summary>
/// A component of a saved entity: its type's name, and its fields as JSON.
/// </summary>
internal sealed record SavedComponent(string Type, JsonElement Values);

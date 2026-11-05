using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Projects;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// The world as data, and as the plain JSON <see cref="World.SaveAsync"/> gives and
/// <see cref="World.RestoreAsync"/> takes. It names a component by its type's name, so it outlives the code that made it.
/// </summary>
internal sealed record WorldState
{
    public required ImmutableArray<WorldContent> Contents { get; init; }

    public required ImmutableArray<ImmutableArray<IComponent>> Entities { get; init; }

    public bool IsPaused { get; init; }

    public float TimeScale { get; init; } = 1;

    /// <summary>
    /// <paramref name="json"/> as a world. A component no code declares, or whose values no longer fit it, is an error,
    /// logged once per type, and the entity goes without it; why not, when it isn't a saved world.
    /// </summary>
    public static Result<WorldState> FromJson(ReadOnlySpan<byte> json, Assemblies code)
    {
        Result<SavedWorld> saved = Json.Read<SavedWorld>(json);
        if (!saved.Ok)
            return Result.Failure(saved.Error);

        HashSet<string> failed = new(StringComparer.Ordinal);
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >(saved.Value.Entities.Length);
        foreach (ImmutableArray<SavedComponent> entity in saved.Value.Entities)
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

            entities.Add(components.ToImmutable());
        }

        return Result.Success(
            new WorldState
            {
                Contents = saved.Value.Contents,
                Entities = entities.MoveToImmutable(),
                IsPaused = saved.Value.IsPaused,
                TimeScale = saved.Value.TimeScale,
            }
        );
    }

    /// <summary>
    /// This world as JSON, or why it can't be. A component JSON cannot hold is an error, logged once per type, and is left
    /// out.
    /// </summary>
    public Result<byte[]> ToJson()
    {
        HashSet<string> failed = new(StringComparer.Ordinal);
        ImmutableArray<ImmutableArray<SavedComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<SavedComponent>
        >(Entities.Length);
        foreach (ImmutableArray<IComponent> entity in Entities)
        {
            ImmutableArray<SavedComponent>.Builder components = ImmutableArray.CreateBuilder<SavedComponent>();
            foreach (IComponent component in entity)
            {
                Type type = component.GetType();
                Result<JsonElement> values = Json.Write(component, type);
                if (values.Ok)
                    components.Add(new SavedComponent(type.Name, values.Value));
                else if (failed.Add(type.Name))
                    Log.Error($"{type.Name} components are not saved, as JSON cannot hold them: {values.Error}");
            }

            entities.Add(components.ToImmutable());
        }

        Result<JsonElement> json = Json.Write(
            new SavedWorld(Contents, entities.MoveToImmutable(), IsPaused, TimeScale),
            typeof(SavedWorld)
        );

        return json.Ok ? Result.Success(Encoding.UTF8.GetBytes(json.Value.GetRawText())) : Result.Failure(json.Error);
    }
}

/// <summary>
/// A saved world as JSON holds it.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it as it reads a saved world."
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

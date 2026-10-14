using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.PrefabLoader;

/// <summary>
/// Reads <c>.prefab</c> files into <see cref="Prefab"/>s (<see cref="IAssetLoader{T}"/>), what the
/// engine spawns: each entity a <see cref="PrefabPart"/>, with its components read as their types
/// (<see cref="Components.Get"/>), its tags as a <see cref="Tags"/>, its scripts made (<see cref="IScripts.Make"/>) with
/// their values set, and the entities under it, a prefab it places among them. What it loads as it reads one is what
/// that one is made from, so it is read again when one of them changes.
/// </summary>
internal sealed class PrefabLoaderGem(Assets assets, IScripts? scripts = null) : IGem, IAssetLoader<Prefab>
{
    /// <summary>
    /// The key of a script entry that names the script, as a handle's does.
    /// </summary>
    private const string ScriptId = "id";

    private readonly Assets _assets = assets;
    private readonly IScripts? _scripts = scripts;

    /// <summary>
    /// The names files have given that are no component, each said once. Read on workers.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _unknown = new(StringComparer.Ordinal);

    public async ValueTask<Result<Prefab>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return Result.Failure<Prefab>($"its .meta holds settings, {settings.GetRawText()}, which a prefab does not take.");
        }

        Result<PrefabFile> read = Json.Read<PrefabFile>(bytes.Span);
        if (!read.Ok)
        {
            return Result.Failure<Prefab>(read.Error);
        }

        Result<List<PrefabPart>> parts = await PartsOfAsync(read.Value.Entities, path, cancel).ConfigureAwait(false);
        return parts.Ok ? Result.Success(new Prefab { Parts = parts.Value }) : Result.Failure<Prefab>(parts.Error);
    }

    private async Task<Result<List<PrefabPart>>> PartsOfAsync(IReadOnlyList<PrefabFileEntity> entities, string path, CancellationToken cancel)
    {
        List<PrefabPart> parts = [];
        foreach (PrefabFileEntity entity in entities)
        {
            Result<PrefabPart> part = await PartOfAsync(entity, path, cancel).ConfigureAwait(false);
            if (!part.Ok)
            {
                return Result.Failure<List<PrefabPart>>(part.Error);
            }

            parts.Add(part.Value);
        }

        return Result.Success(parts);
    }

    /// <summary>
    /// The part <paramref name="entity"/> is: its components, its tags among them, its scripts, and the parts under it,
    /// those of the prefab it places first; why not, when a component is not one, a script is not named by its id, or the
    /// prefab it places does not load. A script that does not load, cannot be made or is given a value it has no
    /// property for is an error, and the part goes without it.
    /// </summary>
    private async Task<Result<PrefabPart>> PartOfAsync(PrefabFileEntity entity, string path, CancellationToken cancel)
    {
        string name = entity.Name.Length > 0 ? entity.Name : "an entity";
        List<IComponent> components = [];
        foreach ((string component, JsonElement json) in entity.Components)
        {
            if (TypeOf(component) is not { } type)
            {
                continue;
            }

            try
            {
                if (json.Deserialize(type, Json.Options) is IComponent value)
                {
                    components.Add(value);
                }
            }
            catch (JsonException ex)
            {
                return Result.Failure<PrefabPart>($"{name}'s {component} is not one: {ex.Message}");
            }
        }

        if (TagsOf(entity, name, path) is { } tags)
        {
            components.Add(tags);
        }

        List<IScript> made = [];
        foreach (IReadOnlyDictionary<string, JsonElement> entry in entity.Scripts)
        {
            if (ScriptOf(entry) is not { } script)
            {
                return Result.Failure<PrefabPart>($"{name} has a script that is not named by its id, {{ \"{ScriptId}\": N }}, a whole number above 0.");
            }

            Result<IScript> one = await MakeAsync(script, entry, cancel).ConfigureAwait(false);
            if (!one.Ok)
            {
                Debugging.Error($"{path}'s {name} goes without script {_assets.Path(script) ?? script.Id.ToString(CultureInfo.InvariantCulture)}: {one.Error}");
                continue;
            }

            made.Add(one.Value);
        }

        List<PrefabPart> children = [];
        if (entity.Prefab != Handle<Prefab>.None)
        {
            Result<Prefab> placed = await _assets.LoadAsync(entity.Prefab, cancel).ConfigureAwait(false);
            if (!placed.Ok)
            {
                return Result.Failure<PrefabPart>($"{name} places a prefab that does not load: {placed.Error}");
            }

            children.AddRange(placed.Value.Parts);
        }

        Result<List<PrefabPart>> under = await PartsOfAsync(entity.Children, path, cancel).ConfigureAwait(false);
        if (!under.Ok)
        {
            return Result.Failure<PrefabPart>(under.Error);
        }

        children.AddRange(under.Value);
        return Result.Success(new PrefabPart { Name = entity.Name, Components = components, Scripts = made, Children = children });
    }

    /// <summary>
    /// The script <paramref name="script"/> made, each key of its entry but the id set on its public property of that
    /// name, read as the property's type; why not, when it does not load, cannot be made, or is given a key it has no
    /// property for or a value that is not one.
    /// </summary>
    private async Task<Result<IScript>> MakeAsync(Handle<Script> script, IReadOnlyDictionary<string, JsonElement> entry, CancellationToken cancel)
    {
        if (_scripts is null)
        {
            return Result.Failure<IScript>("no gem the project lists makes scripts.");
        }

        Result<Script> loaded = await _assets.LoadAsync(script, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            return Result.Failure<IScript>(loaded.Error);
        }

        Result<IScript> made = _scripts.Make(loaded.Value);
        if (!made.Ok)
        {
            return made;
        }

        foreach ((string key, JsonElement json) in entry)
        {
            if (string.Equals(key, ScriptId, StringComparison.Ordinal))
            {
                continue;
            }

            if (PropertyOf(loaded.Value.Class, key) is not { } property)
            {
                return Result.Failure<IScript>($"it is given {key}, which it has no public property with a setter for.");
            }

            try
            {
                property.SetValue(made.Value, json.Deserialize(property.PropertyType, Json.Options));
            }
            catch (JsonException ex)
            {
                return Result.Failure<IScript>($"its {key} is not one: {ex.Message}");
            }
        }

        return made;
    }

    /// <summary>
    /// The component named <paramref name="name"/>; null, and an error logged once, when no component has the name.
    /// </summary>
    private Type? TypeOf(string name)
    {
        if (Components.Get(name) is { } type)
        {
            return type;
        }

        if (_unknown.TryAdd(name, true))
        {
            Debugging.Error($"Prefabs name a component {name}, which is no struct implementing IComponent that Core or a gem declares; it is left out of them.");
        }

        return null;
    }

    /// <summary>
    /// The entity's tags, or null when it has none. Past <see cref="Tags.Capacity"/>, and an empty name, are warned
    /// about and left out.
    /// </summary>
    private static Tags? TagsOf(PrefabFileEntity entity, string name, string path)
    {
        if (entity.Tags.Count == 0)
        {
            return null;
        }

        Tags tags = default;
        foreach (string tag in entity.Tags)
        {
            if (string.IsNullOrEmpty(tag) || (!tags.Add(Tag.Of(tag)) && !tags.Has(Tag.Of(tag))))
            {
                Debugging.Warn($"{path}'s {name} is tagged \"{tag}\", which is left out: a tag needs a name, and an entity has at most {Tags.Capacity}.");
            }
        }

        return tags;
    }

    /// <summary>
    /// <paramref name="type"/>'s public property with a setter that a file names <paramref name="key"/>, in Core's
    /// dialect; null when it has none.
    /// </summary>
    private static PropertyInfo? PropertyOf(Type type, string key)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0
                && string.Equals(Json.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name, key, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// The script a script entry names by its id; null when it names none.
    /// </summary>
    private static Handle<Script>? ScriptOf(IReadOnlyDictionary<string, JsonElement> entry)
    {
        return entry.TryGetValue(ScriptId, out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out ulong value) && value != 0
            ? new Handle<Script>(value)
            : null;
    }
}

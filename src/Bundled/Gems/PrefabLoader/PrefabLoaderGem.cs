using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.PrefabLoader;

/// <summary>
/// Reads <c>.prefab</c> files into <see cref="Prefab"/>s (<see cref="IAssetLoader{T}"/>), what the engine spawns: each
/// entity a <see cref="PrefabPart"/>, with its components read as their types by name
/// (<see cref="Assemblies.Get{T}"/>), its scripts named by the id of their <see cref="Script"/> and their values read
/// as its properties' types (<see cref="PrefabScript"/>), its tags as a <see cref="Tags"/> and its scripts as a
/// <see cref="Scripts"/> among its components, and the entities under it, a prefab it places among them. What it loads
/// as it reads one is what that one is made from, so it is read again when one of them changes.
/// </summary>
internal sealed class PrefabLoaderGem(Assets assets, Assemblies assemblies) : IGem, IAssetLoader<Prefab>
{
    /// <summary>
    /// The key of a script entry that names the script, as a handle's does.
    /// </summary>
    private const string Id = "id";

    private readonly Assets _assets = assets;
    private readonly Assemblies _assemblies = assemblies;

    /// <summary>
    /// The components files name that could not be read, each said once. Read on workers.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _unread = new(StringComparer.Ordinal);

    public async ValueTask<Result<Prefab>> LoadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        JsonElement settings,
        CancellationToken cancel)
    {
        if (settings.GetPropertyCount() != 0)
        {
            return Result.Failure<Prefab>(
                $"its .meta holds settings, {settings.GetRawText()}, which a prefab does not take.");
        }

        Result<PrefabFile> read = Json.Read<PrefabFile>(bytes.Span);

        if (!read.Ok) return Result.Failure<Prefab>(read.Error);

        Result<List<PrefabPart>> parts = await PartsOfAsync(read.Value.Entities, path, cancel).ConfigureAwait(false);

        return parts.Ok ? Result.Success(new Prefab { Parts = parts.Value }) : Result.Failure<Prefab>(parts.Error);
    }

    /// <summary>
    /// The entity's tags, or null when it has none. Past <see cref="Tags.Capacity"/>, and an empty name, are warned
    /// about and left out.
    /// </summary>
    private static Tags? ParseTags(PrefabFileEntity entity, string name, string path)
    {
        if (entity.Tags.Count == 0) return null;

        Tags tags = default;
        foreach (string tag in entity.Tags)
        {
            Tag named = string.IsNullOrEmpty(tag) ? default : Tag.Of(tag);
            if (!tags.Add(named) && !tags.Has(named))
            {
                Debugging.Warn($"{path}'s {name} is tagged \"{tag}\", which is left out: a tag needs a name, and an "
                    + $"entity has at most {Tags.Capacity}.");
            }
        }

        return tags;
    }

    /// <summary>
    /// The entity's <see cref="Scripts"/>, naming each of its part's <paramref name="scripts"/>, or null when it has
    /// none. One named twice, or past <see cref="Scripts.Capacity"/>, is warned about and left out of the part.
    /// </summary>
    private static Scripts? ParseScripts(List<PrefabScript> scripts, string name, string path)
    {
        if (scripts.Count == 0) return null;

        Scripts held = default;
        for (int i = 0; i < scripts.Count; i++)
        {
            if (!held.Add(scripts[i].Script.Handle))
            {
                Debugging.Warn($"{path}'s {name} goes without script {scripts[i].Script.Type.Name}: it is named twice, "
                    + $"or an entity has at most {Scripts.Capacity} scripts.");
                scripts.RemoveAt(i--);
            }
        }

        return held;
    }

    /// <summary>
    /// <paramref name="type"/>'s public property with a setter that a file names <paramref name="key"/>, in Core's
    /// dialect; null when it has none.
    /// </summary>
    private static PropertyInfo? FindProperty(Type type, string key)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0
                && string.Equals(JsonNamingPolicy.CamelCase.ConvertName(property.Name), key, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// The script a script entry names by its id; null when it names none.
    /// </summary>
    private static Handle<Script>? HandleOf(IReadOnlyDictionary<string, JsonElement> entry)
    {
        return entry.TryGetValue(Id, out JsonElement id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetUInt64(out ulong value)
            && value != 0
            ? new Handle<Script>(value)
            : null;
    }

    private async Task<Result<List<PrefabPart>>> PartsOfAsync(
        IReadOnlyList<PrefabFileEntity> entities,
        string path,
        CancellationToken cancel)
    {
        List<PrefabPart> parts = [];
        foreach (PrefabFileEntity entity in entities)
        {
            Result<PrefabPart> part = await PartOfAsync(entity, path, cancel).ConfigureAwait(false);

            if (!part.Ok) return Result.Failure<List<PrefabPart>>(part.Error);

            parts.Add(part.Value);
        }

        return Result.Success(parts);
    }

    /// <summary>
    /// The part <paramref name="entity"/> is: its components, its tags among them, its scripts, and the parts under it,
    /// those of the prefab it places first; why not, when a script is not named by its id, or the prefab it places does
    /// not load. A component no code declares or whose values are not its, logged once, and a script that does not load
    /// or is given a value it does not take, are errors, and the part goes without them.
    /// </summary>
    private async Task<Result<PrefabPart>> PartOfAsync(PrefabFileEntity entity, string path, CancellationToken cancel)
    {
        string name = entity.Name.Length > 0 ? entity.Name : "an entity";
        List<IComponent> components = [];
        foreach ((string component, JsonElement json) in entity.Components)
        {
            Result<Type> type = _assemblies.Get<IComponent>(component);
            Result<object> read = type.Ok ? Json.Read(json, type.Value) : Result.Failure<object>(type.Error);
            if (!read.Ok)
            {
                if (_unread.TryAdd(component, true))
                    Debugging.Error($"{path}'s {name} goes without component {component}: {read.Error}");

                continue;
            }

            components.Add((IComponent)read.Value);
        }

        if (ParseTags(entity, name, path) is { } tags) components.Add(tags);

        List<PrefabScript> scripts = [];
        foreach (IReadOnlyDictionary<string, JsonElement> entry in entity.Scripts)
        {
            if (HandleOf(entry) is not { } handle)
            {
                return Result.Failure<PrefabPart>(
                    $"{name} has a script that is not named by its id, {{ \"{Id}\": N }}, a whole number above 0.");
            }

            Result<PrefabScript> script = await ScriptAsync(handle, entry, cancel).ConfigureAwait(false);
            if (!script.Ok)
            {
                string named = _assets.GetPath(handle) ?? handle.Id.ToString(CultureInfo.InvariantCulture);
                Debugging.Error($"{path}'s {name} goes without script {named}: {script.Error}");

                continue;
            }

            scripts.Add(script.Value);
        }

        if (ParseScripts(scripts, name, path) is { } held) components.Add(held);

        List<PrefabPart> children = [];
        if (entity.Prefab != Handle<Prefab>.None)
        {
            Result<Prefab> placed = await _assets.LoadAsync(entity.Prefab, cancel).ConfigureAwait(false);

            if (!placed.Ok)
                return Result.Failure<PrefabPart>($"{name} places a prefab that does not load: {placed.Error}");

            children.AddRange(placed.Value.Parts);
        }

        Result<List<PrefabPart>> under = await PartsOfAsync(entity.Children, path, cancel).ConfigureAwait(false);

        if (!under.Ok) return Result.Failure<PrefabPart>(under.Error);

        children.AddRange(under.Value);

        return Result.Success(
            new PrefabPart { Name = entity.Name, Components = components, Scripts = scripts, Children = children });
    }

    /// <summary>
    /// The script <paramref name="handle"/> declares as a part holds it: loaded, with each key of its entry but the id
    /// as the value of its public property of that name, read as the property's type; why not, when it does not load,
    /// declares no script, or is given a key it has no property for or a value that is not one.
    /// </summary>
    private async Task<Result<PrefabScript>> ScriptAsync(
        Handle<Script> handle,
        IReadOnlyDictionary<string, JsonElement> entry,
        CancellationToken cancel)
    {
        Result<Script> loaded = await _assets.LoadAsync(handle, cancel).ConfigureAwait(false);

        if (!loaded.Ok) return Result.Failure<PrefabScript>(loaded.Error);

        Type type = loaded.Value.Type;

        if (!type.IsAssignableTo(typeof(IScript)))
            return Result.Failure<PrefabScript>($"{type.Name} is no script: a script is a class implementing IScript.");

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement json) in entry)
        {
            if (string.Equals(key, Id, StringComparison.Ordinal)) continue;

            if (FindProperty(type, key) is not { } property)
            {
                return Result.Failure<PrefabScript>(
                    $"it is given {key}, which it has no public property with a setter for.");
            }

            try
            {
                values[property.Name] = json.Deserialize(property.PropertyType, Json.Options);
            }
            catch (JsonException ex)
            {
                return Result.Failure<PrefabScript>($"its {key} is not one: {ex.Message}");
            }
        }

        return Result.Success(new PrefabScript(loaded.Value, values));
    }
}

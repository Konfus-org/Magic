using System.Collections.Immutable;
using System.CommandLine;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query domain</c>: prints the open domain as the engine resolves it, as one JSON object.
/// </summary>
/// <remarks>
/// A tool that draws the same scene reads this file, so that no tool reads a prefab, a material or a <c>.meta</c>
/// itself. The values are Magic's own: left-handed, +Y up, metres, and the units of each component. Every entity of
/// the live world is in it, with its world transform, its components as <c>query</c> prints them, and each handle as its
/// id, path and type. A field that holds the default of its type is left out, as a file leaves it out; the components
/// query prints the defaults.
/// </remarks>
internal static class DomainQuery
{
    public static Command Create(
        App app,
        IFileSystem files,
        AssetManager assets,
        World world,
        CommandQueue queue,
        Option<string> output
    )
    {
        Option<string[]> names = new("--names")
        {
            Description = "Only the entities whose name starts with one of these, as --names ReferenceView Cabin",
            AllowMultipleArgumentsPerToken = true,
        };
        Option<string[]> tags = new("--tags")
        {
            Description = "Only the entities that hold one of these tags, as --tags render-reference spawn",
            AllowMultipleArgumentsPerToken = true,
        };
        Command domain = new(
            "domain",
            "Prints the open domain as the engine resolves it, as JSON: the project, and every entity with its world transform, components and materials"
        )
        {
            names,
            tags,
        };
        domain.SetAction(parsed =>
            Describe(parsed.GetValue(output), parsed.GetValue(names) ?? [], parsed.GetValue(tags) ?? [])
        );

        return domain;

        void Describe(string? file, string[] named, string[] tagged)
        {
            if (app.Project is not { } project)
            {
                Log.Error("No project is running to print its domain.");

                return;
            }

            // The ECS is read on this thread; the assets that name and resolve what it holds are loaded after.
            ImmutableArray<Spawned> spawned = ReadWorld(world);
            Task writing = DescribeAsync(project, spawned, file, named, tagged);
            writing.FireAndForget();
            queue.Hold(() => !writing.IsCompleted);
        }

        async Task DescribeAsync(
            Project project,
            ImmutableArray<Spawned> spawned,
            string? file,
            string[] named,
            string[] tagged
        )
        {
            // The entities come first: each material that a slot of theirs holds is resolved while they are walked,
            // and the materials block then holds each one time, however many entities draw with it.
            Dictionary<ulong, Material> used = [];
            JsonArray entities = await DescribeEntitiesAsync(spawned, named, tagged, used).ConfigureAwait(false);
            JsonObject described = new()
            {
                ["project"] = DescribeProject(project),
                ["entities"] = entities,
                ["materials"] = DescribeMaterials(used),
                ["models"] = await DescribeModelsAsync(spawned).ConfigureAwait(false),
            };
            CommandLookups.Print(files, file, described, "The domain is in");
        }

        // The project: its name, its file, where its screenshots go, and the domains that the world holds.
        JsonObject DescribeProject(Project project) =>
            new()
            {
                ["name"] = project.Name,
                ["path"] = project.Path,
                ["screenshots"] = ScreenshotCommand.GetFolder(files),
                ["entryPoint"] = DescribeHandle(project.EntryPoint.Id),
                ["domains"] = new JsonArray([.. world.Domains.Select(open => DescribeHandle(open.Id))]),
            };

        async Task<JsonArray> DescribeEntitiesAsync(
            ImmutableArray<Spawned> spawned,
            string[] named,
            string[] tagged,
            Dictionary<ulong, Material> used
        )
        {
            ImmutableDictionary<ulong, ImmutableArray<PlacedPart>> prefabs = await ReadPrefabsAsync(spawned)
                .ConfigureAwait(false);

            // The engine hashes each name asked for, so no caller holds a hash of its own.
            Tag[] asked = [.. tagged.Where(tag => tag.Length > 0).Select(Tag.From)];
            JsonArray entities = [];
            foreach (Spawned entity in spawned)
            {
                PlacedPart? part = FindPart(prefabs, entity);
                string name = part?.Part.Name ?? string.Empty;
                if (named.Length > 0 && !named.Any(start => name.StartsWith(start, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Tags tags = entity.Components.OfType<Tags>().FirstOrDefault();
                if (asked.Length > 0 && !asked.Any(tags.Has))
                    continue;

                JsonObject described = new()
                {
                    ["id"] = entity.Id,
                    ["name"] = name,
                    ["components"] = DescribeComponents(entity),
                };
                if (DescribeTags(tags) is { Count: > 0 } tagNames)
                    described["tags"] = tagNames;

                if (part is { } placed && DescribeBase(entity, placed) is { } held)
                    described["base"] = held;

                if (
                    entity.Components.OfType<Renderer>().FirstOrDefault() is { Materials.Count: > 0 } renderer
                    && await DescribeSlotsAsync(renderer, used).ConfigureAwait(false) is { Count: > 0 } slots
                )
                    described["slots"] = slots;

                entities.Add(described);
            }

            return entities;
        }

        // The name of each tag of the entity that this run knows, so a tool reads a tag without the hash. A tag that
        // only a saved world holds has no name and is left out; its id stays in the Tags component.
        static JsonArray DescribeTags(Tags tags)
        {
            JsonArray names = [];
            foreach (Tag tag in tags)
            {
                if (tag.GetName() is { } name)
                    names.Add(name);
            }

            return names;
        }

        // Each prefab that a domain placed, with its parts where the domain put them: the globals at the domain's
        // origin and a chunk's prefab at its corner, so a part's transform is in the world as its entity's is.
        async Task<ImmutableDictionary<ulong, ImmutableArray<PlacedPart>>> ReadPrefabsAsync(
            ImmutableArray<Spawned> spawned
        )
        {
            Dictionary<ulong, Transform> places = [];
            foreach (Handle<Domain> open in world.Domains)
            {
                Result<Domain> loaded = await assets.LoadAsync(open, null, app.Lifetime).ConfigureAwait(false);
                if (!loaded.Ok)
                {
                    Log.Error($"The entities of domain {open.Id} go without their names: {loaded.Error}");

                    continue;
                }

                foreach (Handle<Prefab> global in loaded.Value.Globals)
                {
                    places[global.Id] = new Transform();
                }

                foreach (Chunk chunk in loaded.Value.Chunks)
                {
                    places[chunk.Prefab.Id] = chunk.At;
                }
            }

            ImmutableDictionary<ulong, ImmutableArray<PlacedPart>>.Builder prefabs = ImmutableDictionary.CreateBuilder<
                ulong,
                ImmutableArray<PlacedPart>
            >();
            foreach (ulong id in spawned.Select(entity => entity.Owner?.Prefab.Id ?? 0).Where(id => id != 0).Distinct())
            {
                Result<Prefab> loaded = await assets
                    .LoadAsync(new Handle<Prefab>(id), null, app.Lifetime)
                    .ConfigureAwait(false);
                if (loaded.Ok)
                    prefabs[id] = loaded.Value.Place(places.GetValueOrDefault(id, new Transform()));
                else
                    Log.Error($"The entities of prefab {id} go without their names: {loaded.Error}");
            }

            return prefabs.ToImmutable();
        }

        // Each component of the entity, by its type's name, with only the fields that differ from the type's defaults.
        JsonObject DescribeComponents(Spawned entity)
        {
            JsonObject components = [];
            foreach (IComponent component in entity.Components)
            {
                Type type = component.GetType();
                if (Written(component, type, includesDefaults: false) is not { } set)
                    continue;

                components[type.Name] = set;
                Resolve(set);
            }

            return components;
        }

        // What the prefab holds where the live world holds something else: only the fields that differ, by the type's
        // name. A script that moves a light or an entity shows here as the value it started from.
        JsonObject? DescribeBase(Spawned entity, PlacedPart part)
        {
            JsonObject held = [];
            foreach (IComponent component in part.Part.Components)
            {
                Type type = component.GetType();

                // The part holds its transform in the prefab; the world holds it where the domain put the prefab.
                IComponent start = component is Transform ? part.At : component;
                if (
                    entity.Components.FirstOrDefault(live => live.GetType() == type) is not { } now
                    || Written(start, type, includesDefaults: true) is not { } before
                    || Written(now, type, includesDefaults: true) is not { } live
                )
                    continue;

                if (Without(before, live) is { } differs)
                {
                    held[type.Name] = differs;
                    Resolve(held[type.Name]);
                }
            }

            return held.Count > 0 ? held : null;
        }

        // What a slot of the renderer holds over its material, by the number of the slot: only the slots that override
        // a param, as the handle of each slot is in the Renderer component already. It reads each material the slots
        // name into used, for the materials block.
        async Task<JsonArray> DescribeSlotsAsync(Renderer renderer, Dictionary<ulong, Material> used)
        {
            JsonArray slots = [];
            int number = 0;
            foreach (MaterialInstance slot in renderer.Materials.All)
            {
                if (slot.Handle != Handle<Material>.None && !used.ContainsKey(slot.Handle.Id))
                {
                    Result<Material> read = await assets
                        .LoadAsync(slot.Handle, null, app.Lifetime)
                        .ConfigureAwait(false);
                    if (read.Ok)
                        used[slot.Handle.Id] = read.Value;
                    else
                        Log.Error($"Material {slot.Handle.Id} goes without its values: {read.Error}");
                }

                if (Resolved(slot.Overrides, typeof(MaterialOverrides)) is { } overrides)
                {
                    slots.Add(new JsonObject { ["slot"] = number, ["overrides"] = overrides });
                }

                number++;
            }

            return slots;
        }

        // Each material that the world draws with, one time, by its handle: the shader, the transparency, the sides
        // and the params of its shader's struct that it sets.
        JsonArray DescribeMaterials(Dictionary<ulong, Material> used)
        {
            JsonArray materials = [];
            foreach (KeyValuePair<ulong, Material> pair in used.OrderBy(pair => pair.Key))
            {
                if (Resolved(pair.Value, typeof(Material)) is not { } values)
                    continue;

                materials.Add(new JsonObject { ["handle"] = DescribeHandle(pair.Key), ["material"] = values });
            }

            return materials;
        }

        // Each model that a renderer draws, one time: where its origin is, and the material slot of each of its
        // meshes, in the order that the model holds them. What the importer did to the file's axes, units and texture
        // coordinates is the importer's own, and its gem prints it.
        async Task<JsonArray> DescribeModelsAsync(ImmutableArray<Spawned> spawned)
        {
            JsonArray models = [];
            IEnumerable<ulong> drawn = spawned
                .SelectMany(entity => entity.Components.OfType<Renderer>())
                .Select(renderer => renderer.Model.Id)
                .Where(id => id != 0)
                .Distinct();
            foreach (ulong id in drawn)
            {
                Result<Model> loaded = await assets
                    .LoadAsync(new Handle<Model>(id), null, app.Lifetime)
                    .ConfigureAwait(false);
                if (!loaded.Ok)
                {
                    Log.Error($"Model {id} is left out: {loaded.Error}");

                    continue;
                }

                JsonArray meshes = [];
                foreach (Mesh mesh in loaded.Value.Meshes)
                {
                    meshes.Add(
                        new JsonObject
                        {
                            ["materialSlot"] = mesh.MaterialSlot,
                            ["vertices"] = mesh.Vertices.Length,
                            ["triangles"] = mesh.Indices.Length / 3,
                        }
                    );
                }

                models.Add(
                    new JsonObject
                    {
                        ["handle"] = DescribeHandle(id),
                        ["origin"] = Resolved(loaded.Value.Origin, typeof(Vector3)),
                        ["meshes"] = meshes,
                    }
                );
            }

            return models;
        }

        // value as JSON with only what differs from the defaults of its type, and each handle in it resolved; null
        // when it holds nothing but defaults.
        JsonNode? Resolved(object value, Type type)
        {
            if (Written(value, type, includesDefaults: false) is not { } set || set is JsonObject { Count: 0 })
                return null;

            Resolve(set);

            return set;
        }

        // The part of a prefab that spawned the entity, for an entity that a domain placed.
        static PlacedPart? FindPart(ImmutableDictionary<ulong, ImmutableArray<PlacedPart>> prefabs, Spawned entity) =>
            entity.Owner is { } owner
            && prefabs.TryGetValue(owner.Prefab.Id, out ImmutableArray<PlacedPart> parts)
            && owner.Index < parts.Length
                ? parts[owner.Index]
                : null;

        // Adds the path and the type of the asset to each handle under the node, which a file writes as its id alone.
        // A handle is any object with an id that an asset has, so the id of a script that a container holds is
        // resolved as the id of a material is.
        void Resolve(JsonNode? node)
        {
            if (node is JsonArray items)
            {
                foreach (JsonNode? item in items)
                {
                    Resolve(item);
                }

                return;
            }

            if (node is not JsonObject held)
                return;

            if (held["id"] is JsonValue value && value.TryGetValue(out ulong id))
                AddAsset(held, id);

            foreach (KeyValuePair<string, JsonNode?> pair in held.ToArray())
            {
                Resolve(pair.Value);
            }
        }

        // The asset with the id: its id, its path and its type; the id alone for one that no file has.
        JsonObject DescribeHandle(ulong id)
        {
            JsonObject described = new() { ["id"] = id };
            AddAsset(described, id);

            return described;
        }

        void AddAsset(JsonObject handle, ulong id)
        {
            if (!assets.Database.Index.ById.TryGetValue(id, out IndexedFile? found))
                return;

            handle["path"] = found.Path;
            handle["type"] = found.Type;
        }
    }

    /// <summary>
    /// <paramref name="value"/> as a JSON node of its own, with or without the defaults of its type as a file is
    /// written, or none when JSON cannot hold it, which it logs.
    /// </summary>
    private static JsonNode? Written(object value, Type type, bool includesDefaults)
    {
        Result<JsonElement> written = Json.Write(value, type, includesDefaults);
        if (!written.Ok)
        {
            Log.Error($"A {type.Name} is left out: {written.Error}");

            return null;
        }

        return JsonNode.Parse(written.Value.GetRawText());
    }

    /// <summary>
    /// <paramref name="value"/> with each field that <paramref name="other"/> holds the same left out; null when the
    /// two are the same. A field that the other does not have at all stays.
    /// </summary>
    private static JsonNode? Without(JsonNode? value, JsonNode? other)
    {
        if (other is null)
            return value;

        if (value is not JsonObject held || other is not JsonObject theirs)
            return JsonNode.DeepEquals(value, other) ? null : value;

        // The field leaves the value first: what is left holds the nodes of the value itself, and a node has one
        // parent.
        JsonObject left = [];
        foreach (KeyValuePair<string, JsonNode?> pair in held.ToArray())
        {
            held.Remove(pair.Key);
            if (Without(pair.Value, theirs[pair.Key]) is { } differs)
                left[pair.Key] = differs;
        }

        return left.Count > 0 ? left : null;
    }

    /// <summary>
    /// Each entity of <paramref name="world"/> with its components, boxed copies to print.
    /// </summary>
    private static ImmutableArray<Spawned> ReadWorld(World world)
    {
        List<Entity> alive = [];
        foreach (EcsChunk chunk in world.Query())
        {
            alive.AddRange(chunk.Entities);
        }

        return [.. alive.Select(entity => new Spawned(entity.Id, [.. world.GetComponents(entity)]))];
    }

    /// <summary>
    /// One entity of the live world, read on the game thread: its id and its components.
    /// </summary>
    private sealed record Spawned(int Id, ImmutableArray<IComponent> Components)
    {
        // What placed it, for an entity that a domain's files placed.
        public Owner? Owner => Components.OfType<Owner>().Select(owner => (Owner?)owner).FirstOrDefault();
    }
}

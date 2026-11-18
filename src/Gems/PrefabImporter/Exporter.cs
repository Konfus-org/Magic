using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;

namespace Magic.PrefabImporter;

/// <summary>
/// Writes a <see cref="Prefab"/> as the <c>.prefab</c> file that the <see cref="Importer"/> reads.
/// </summary>
/// <remarks>
/// A part that places a prefab is written with the handle of that prefab, and without the parts that the prefab
/// brings. A field of a component that holds the default of its type is left out, unless the export asks for the
/// defaults. A tag that this run has no name for is left out.
/// </remarks>
public sealed class Exporter : IAssetExporter<Prefab>
{
    private readonly AssetManager _assets;

    internal Exporter(AssetManager assets) => _assets = assets;

    public string Extension => ".prefab";

    public async Task<Result<ReadOnlyMemory<byte>>> ExportAsync(
        Prefab asset,
        bool includesDefaults,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        // One entity to a line, which keeps the file readable and its differences short.
        StringBuilder file = new("{\n    \"entities\": [");
        for (int i = 0; i < asset.Parts.Length; i++)
        {
            Result<JsonObject> entity = await WritePartAsync(asset.Parts[i], includesDefaults, cancel)
                .ConfigureAwait(false);
            if (!entity.Ok)
                return Result.Failure(entity.Error);

            file.Append(i == 0 ? "\n        " : ",\n        ").Append(entity.Value.ToJsonString(Json.Options));
        }

        file.Append("\n    ]\n}\n");

        return Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(file.ToString()));
    }

    /// <summary>
    /// The entity of a file that <paramref name="part"/> is read from: its name, the prefab that it places, its tags,
    /// its components and the entities under it.
    /// </summary>
    private async Task<Result<JsonObject>> WritePartAsync(
        PrefabPart part,
        bool includesDefaults,
        CancellationToken cancel
    )
    {
        JsonObject entity = [];
        if (part.Name.Length > 0)
            entity["name"] = part.Name;

        // The parts of the prefab that it places are the first under it, and the file holds only the handle.
        int placedParts = 0;
        if (part.Placed != Handle<Prefab>.None)
        {
            Result<Prefab> placed = await _assets.LoadAsync(part.Placed, null, cancel).ConfigureAwait(false);
            if (!placed.Ok)
                return Result.Failure($"{part.Name} places a prefab that does not load: {placed.Error}");

            entity["prefab"] = new JsonObject { ["id"] = part.Placed.Id };
            placedParts = placed.Value.Parts.Length;
        }

        JsonObject components = [];
        foreach (IComponent component in part.Components)
        {
            if (component is Tags tags)
            {
                JsonArray names = [];
                foreach (Tag tag in tags)
                {
                    if (tag.GetName() is { } name)
                        names.Add(name);
                }

                entity["tags"] = names;

                continue;
            }

            Result<JsonElement> written = Json.Write(component, component.GetType(), includesDefaults);
            if (!written.Ok)
                return Result.Failure($"{part.Name}'s {component.GetType().Name} was not written: {written.Error}");

            // A component is an object, or a list as a container of scripts is.
            components[component.GetType().Name] = JsonNode.Parse(written.Value.GetRawText());
        }

        if (components.Count > 0)
            entity["components"] = components;

        JsonArray children = [];
        foreach (PrefabPart child in part.Children.Skip(placedParts))
        {
            Result<JsonObject> written = await WritePartAsync(child, includesDefaults, cancel).ConfigureAwait(false);
            if (!written.Ok)
                return written;

            children.Add(written.Value);
        }

        if (children.Count > 0)
            entity["children"] = children;

        return Result.Success(entity);
    }
}

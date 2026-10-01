using Magic.Attributes;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A list of entities on disk, one file per cube of a <see cref="World"/>. A chunk named <c>x_y_z.chunk</c>
/// (signed integers) sits at that grid coordinate and streams in and out with the cameras; any other name
/// (<c>globals.chunk</c> by convention) is loaded with the world and stays. Only the streaming system reads
/// the entities; the ECS never sees a chunk.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Chunk : Asset
{
    public Entity[] Entities { get; set; } = [];

    /// <summary>The grid coordinate parsed from the file name (<c>x_y_z</c>); null for a global chunk.</summary>
    [JsonIgnore]
    public (int X, int Y, int Z)? Coordinate => ParseCoordinate(Name);

    /// <summary>The coordinate a chunk file name denotes, or null when the name is not <c>x_y_z</c>.</summary>
    public static (int X, int Y, int Z)? ParseCoordinate(string name)
    {
        string[] parts = name.Split('_');
        if (parts.Length != 3)
            return null;

        if (int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z))
            return (x, y, z);

        return null;
    }

    /// <summary>The file name a coordinate's chunk has, without the folder.</summary>
    public static string FileName(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"{x}_{y}_{z}.chunk");

    /// <summary>
    /// One entity as a chunk file holds it. This is data in a file; a live entity is a <see cref="Handle"/>.
    /// A component is written under its type name (<c>Transform</c>, <c>DirectionalLight</c>; <c>MyGem.Health</c>
    /// when two gems share a short name) as the struct's own JSON, and stays JSON here because a file cannot
    /// name a struct without a converter; whoever spawns the entity resolves the name.
    /// </summary>
    public sealed class Entity
    {
        private Dictionary<string, JsonElement> _components = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>0 = none; otherwise stable within the world and stored as <see cref="Components.EntityId"/>.</summary>
        public ulong Id { get; set; }

        /// <summary>Unique among siblings, no '.'; unnamed is fine (the grid samples name nothing).</summary>
        public string? Name { get; set; }

        /// <summary>Applied with <see cref="Components.Tag.Of"/>; more than <see cref="Components.Tags.Capacity"/> is a warning and the rest are dropped.</summary>
        public string[] Tags { get; set; } = [];

        /// <summary>Keys are matched without regard to case; the serializer's own dictionary is rewrapped to keep it so.</summary>
        public Dictionary<string, JsonElement> Components
        {
            get => _components;
            set => _components = value.Comparer == StringComparer.OrdinalIgnoreCase
                ? value
                : new Dictionary<string, JsonElement>(value, StringComparer.OrdinalIgnoreCase);
        }

        public Entity[] Children { get; set; } = [];
    }
}

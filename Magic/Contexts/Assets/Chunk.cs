using Magic.Attributes.Assets;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A list of entities on disk, one file per cube of a <see cref="Domain"/>. A chunk named <c>x_y_z.chunk</c>
/// (signed integers) sits at that grid coordinate and streams in and out with the cameras; any other name
/// (<c>globals.chunk</c> by convention) is loaded with the domain and stays. Only the spawner reads
/// the entities; the ECS never sees a chunk.
/// <para>
/// The file is JSON, <c>{ "entities": [ ... ] }</c>, and a loaded chunk is that file's bytes and nothing more
/// (<see cref="Data"/>): thousands stream in and out, and the spawner reads the bytes straight into components
/// without anything in between. <see cref="Entities"/> is the same file as objects, read the first time it is asked
/// for, for whoever would rather walk it (a stand-in generator) and for writing a chunk.
/// </para>
/// </summary>
[AssetFormat(AssetFormat.Binary)]
public sealed class Chunk : Asset
{
    private Entity[]? _entities;

    /// <summary>
    /// The file as it is on disk: UTF-8 JSON.
    /// </summary>
    [JsonIgnore]
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// <see cref="Data"/> without a byte order mark, as a JSON reader wants it.
    /// </summary>
    [JsonIgnore]
    public ReadOnlySpan<byte> Json => Data.AsSpan().StartsWith(Bom) ? Data.AsSpan(Bom.Length) : Data;

    public Entity[] Entities
    {
        get => _entities ??= Data.Length == 0 ? [] : JsonSerializer.Deserialize<File>(Json, AssetJson.Options)?.Entities ?? [];
        set => _entities = value;
    }

    /// <summary>
    /// The grid coordinate parsed from the file name (<c>x_y_z</c>); null for a global chunk.
    /// </summary>
    [JsonIgnore]
    public (int X, int Y, int Z)? Coordinate => ParseCoordinate(Name);

    /// <summary>
    /// The coordinate a chunk file name denotes, or null when the name is not <c>x_y_z</c>.
    /// </summary>
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

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The file name a coordinate's chunk has, without the folder.
    /// </summary>
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

        /// <summary>
        /// 0 = none; otherwise stable within the world and stored as <see cref="Components.EntityId"/>.
        /// </summary>
        public ulong Id { get; set; }

        /// <summary>
        /// Unique among siblings, no '.'; unnamed is fine (the grid samples name nothing).
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Applied with <see cref="Components.Tag.Of"/>; more than <see cref="Components.Tags.Capacity"/> is a warning and the rest are dropped.
        /// </summary>
        public string[] Tags { get; set; } = [];

        /// <summary>
        /// Keys are matched without regard to case; the serializer's own dictionary is rewrapped to keep it so.
        /// </summary>
        public Dictionary<string, JsonElement> Components
        {
            get => _components;
            set => _components = value.Comparer == StringComparer.OrdinalIgnoreCase
                ? value
                : new Dictionary<string, JsonElement>(value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One object per script: the <c>id</c> of its <see cref="Script"/> asset, and beside it the values of the
        /// instance's public fields and properties. JSON for the reason <see cref="Components"/> is: only the script's
        /// class, known once it is loaded, says what they are.
        /// </summary>
        public JsonElement[] Scripts { get; set; } = [];

        public Entity[] Children { get; set; } = [];
    }

    /// <summary>
    /// What a chunk file holds.
    /// </summary>
    private sealed class File
    {
        public Entity[] Entities { get; set; } = [];
    }
}

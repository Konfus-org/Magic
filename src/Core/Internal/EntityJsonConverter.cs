using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Contexts.ECS;

namespace Magic.Internal;

/// <summary>
/// An entity as JSON: its id, which is what a saved world (<see cref="WorldState"/>) holds for an entity a component
/// or a script refers to. One is read only as the world is restored, which maps each id to the entity it made for it
/// (<see cref="Remap"/>) on the thread it restores on; an id the save holds no entity for, one destroyed before it,
/// reads as none. Anywhere else, reading one is an error: a file names no entity, as its slot is nothing outside the
/// run that gave it.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as Entity's [JsonConverter] names it.")]
internal sealed class EntityJsonConverter : JsonConverter<Entity>
{
    /// <summary>
    /// The entity each saved id is now, while the world is restored on this thread; null otherwise.
    /// </summary>
    [field: ThreadStatic]
    internal static Dictionary<int, Entity>? Remap { get; set; }

    public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (Remap is not { } remap)
        {
            throw new JsonException("An entity is read only as the world is restored: a file names no entity.");
        }

        return remap.TryGetValue(reader.GetInt32(), out Entity entity) ? entity : default;
    }

    public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Id);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Ecs;

/// <summary>
/// A handle to the components that the <see cref="IEcs"/> holds for one entity.
/// </summary>
/// <remarks>
/// It is three ints in the order that the ECS gem uses for its own entities, so a query gives out the entity arrays of
/// the gem without a copy.
/// </remarks>
/// <param name="Id">Its slot, which a later entity reuses once it is destroyed.</param>
/// <param name="EcsWorld">Which of the ECS gem's own worlds it lives in, kept for its layout.</param>
/// <param name="Version">How many entities have had its slot, so a handle to a destroyed entity is never taken for
/// the one in its slot now.</param>
[JsonConverter(typeof(EntityJsonConverter))]
public readonly record struct Entity(int Id, int EcsWorld, int Version);

/// <summary>
/// Writes an entity as its id, and reads it back as no entity, because an id has no meaning after its run.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as a [JsonConverter] names it."
)]
internal sealed class EntityJsonConverter : JsonConverter<Entity>
{
    public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        default;

    public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Id);
}

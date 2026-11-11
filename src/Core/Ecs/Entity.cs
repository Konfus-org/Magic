using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Ecs;

/// <summary>
/// An entity: a handle to the components the <see cref="IEcs"/> holds for it. Three ints in this order, as the ECS gem
/// lays out its own, so a query hands out the gem's entity arrays without copying them.
/// </summary>
/// <param name="Id">Its slot, which a later entity reuses once it is destroyed.</param>
/// <param name="EcsWorld">Which of the ECS gem's own worlds it lives in, kept for its layout.</param>
/// <param name="Version">How many entities have had its slot, so a handle to a destroyed entity is never taken for
/// the one in its slot now.</param>
[JsonConverter(typeof(EntityJsonConverter))]
public readonly record struct Entity(int Id, int EcsWorld, int Version);

/// <summary>
/// An entity as JSON: its id, which means nothing once the run that gave it ends, so it reads back as none.
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

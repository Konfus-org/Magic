using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;

namespace Magic.Internal;

/// <summary>
/// A renderer's material slots as JSON: an array of handles by slot, <c>[ { "id": 710 } ]</c>, so a prefab lists a
/// model's materials in the order its meshes name them. A slot left empty before a filled one is written as none,
/// <c>{ "id": 0 }</c>, and read back as one, so a saved world keeps every slot where it was; the empty slots after the
/// last filled one are left out. More than <see cref="MaterialSlots.Capacity"/> is refused. The serializer's own
/// form, as the slots are enumerable, writes all of them and cannot read them back.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as MaterialSlots' [JsonConverter] names it.")]
internal sealed class MaterialSlotsJsonConverter : JsonConverter<MaterialSlots>
{
    public override MaterialSlots Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Material slots are an array of handles.");

        MaterialSlots slots = default;
        int slot = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (slot == MaterialSlots.Capacity)
                throw new JsonException($"Material slots are at most {MaterialSlots.Capacity} handles.");

            slots[slot++] = JsonSerializer.Deserialize<Handle<Material>>(ref reader, options);
        }

        return slots;
    }

    public override void Write(Utf8JsonWriter writer, MaterialSlots value, JsonSerializerOptions options)
    {
        int count = MaterialSlots.Capacity;
        while (count > 0 && value[count - 1] == Handle<Material>.None)
        {
            count--;
        }

        writer.WriteStartArray();
        for (int i = 0; i < count; i++)
        {
            JsonSerializer.Serialize(writer, value[i], options);
        }

        writer.WriteEndArray();
    }
}

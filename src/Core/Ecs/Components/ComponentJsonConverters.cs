using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// An entity's tags as JSON: an array of their ids, as a saved world holds them. The serializer has no form for an inline
/// array. Past <see cref="Tags.Capacity"/>, the rest are left out.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as a [JsonConverter] names it."
)]
internal sealed class TagsJsonConverter : JsonConverter<Tags>
{
    public override Tags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Tags are an array of ids.");

        Tags tags = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            tags.Add(new Tag(reader.GetInt32()));
        }

        return tags;
    }

    public override void Write(Utf8JsonWriter writer, Tags value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (Tag tag in value)
        {
            if (tag.IsValid)
                writer.WriteNumberValue(tag.Id);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// A renderer's material slots as JSON: an array of handles by slot, so a prefab lists a model's materials in the order
/// its meshes name them. Empty slots after the last filled one are left out.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as a [JsonConverter] names it."
)]
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

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// Reads and writes the tags of an entity as JSON: an array of their ids, as a saved world holds them.
/// </summary>
/// <remarks>
/// The serializer has no form for an inline array. The tags after <see cref="Tags.Capacity"/> are left out.
/// </remarks>
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
/// Reads and writes material slots as JSON: an array of instances by slot, in the order of the component.
/// </summary>
/// <remarks>
/// A prefab lists the materials of a model in the order that its meshes name them. The empty slots after the last
/// filled slot are left out.
/// </remarks>
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
            throw new JsonException(
                "Material slots are an array: a material's handle each, or its handle and overrides."
            );

        MaterialSlots slots = default;
        int slot = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (slot == MaterialSlots.Capacity)
                throw new JsonException($"Material slots are at most {MaterialSlots.Capacity}.");

            // A slot is its material's handle alone, or an object of its handle and overrides.
            JsonElement element = JsonSerializer.Deserialize<JsonElement>(ref reader, options);
            if (!element.TryGetProperty("handle", out JsonElement handle))
            {
                slots[slot++] = new MaterialInstance { Handle = element.Deserialize<Handle<Material>>(options) };

                continue;
            }

            slots[slot++] = new MaterialInstance
            {
                Handle = handle.Deserialize<Handle<Material>>(options),
                Overrides = element.TryGetProperty("overrides", out JsonElement overrides)
                    ? overrides.Deserialize<MaterialOverrides>(options)
                    : default,
            };
        }

        return slots;
    }

    public override void Write(Utf8JsonWriter writer, MaterialSlots value, JsonSerializerOptions options)
    {
        int count = MaterialSlots.Capacity;
        while (count > 0 && value[count - 1].Handle == Handle<Material>.None)
        {
            count--;
        }

        writer.WriteStartArray();
        for (int i = 0; i < count; i++)
        {
            // A slot that sets no params is its material's handle alone.
            MaterialInstance instance = value[i];
            if (instance.Overrides.Params.IsEmpty)
            {
                JsonSerializer.Serialize(writer, instance.Handle, options);

                continue;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("handle");
            JsonSerializer.Serialize(writer, instance.Handle, options);
            writer.WritePropertyName("overrides");
            JsonSerializer.Serialize(writer, instance.Overrides, options);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads and writes the material set of a renderer as JSON, as the array of <see cref="MaterialSlotsJsonConverter"/>.
/// </summary>
/// <remarks>
/// The process keeps each set that it reads.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as a [JsonConverter] names it."
)]
internal sealed class MaterialSetJsonConverter : JsonConverter<MaterialSet>
{
    public override MaterialSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        MaterialSlots slots = JsonSerializer.Deserialize<MaterialSlots>(ref reader, options);

        return MaterialSet.Read(slots);
    }

    public override void Write(Utf8JsonWriter writer, MaterialSet value, JsonSerializerOptions options)
    {
        MaterialSlots slots = default;
        value.All.AsSpan().CopyTo(slots);
        JsonSerializer.Serialize(writer, slots, options);
    }
}

internal sealed class MaterialOverridesJsonConverter : JsonConverter<MaterialOverrides>
{
    public override MaterialOverrides Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Material overrides are an object of params by name.");

        MaterialOverrides overrides = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? throw new JsonException("A material param has no name.");
            reader.Read();
            overrides[name] =
                JsonSerializer.Deserialize<MaterialParam>(ref reader, options)
                ?? throw new JsonException($"The material param {name} is null.");
        }

        return overrides;
    }

    public override void Write(Utf8JsonWriter writer, MaterialOverrides value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((string name, MaterialParam param) in value.Params)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, param, options);
        }

        writer.WriteEndObject();
    }
}

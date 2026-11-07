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
/// Material slots as JSON: an array of instances by slot, in the order the component lists them, so a prefab lists a
/// model's materials in the order its meshes name them. Empty slots after the last filled one are left out.
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
            throw new JsonException(
                "Material slots are an array: a material's handle each, or its handle and overrides."
            );

        MaterialSlots slots = default;
        int slot = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (slot == MaterialSlots.Capacity)
                throw new JsonException($"Material slots are at most {MaterialSlots.Capacity}.");

            slots[slot++] = ReadInstance(ref reader, options);
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
            WriteInstance(writer, value[i], options);
        }

        writer.WriteEndArray();
    }

    private static MaterialInstance ReadInstance(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        JsonElement element = JsonSerializer.Deserialize<JsonElement>(ref reader, options);
        if (!element.TryGetProperty("handle", out JsonElement handle))
            return new MaterialInstance { Handle = element.Deserialize<Handle<Material>>(options) };

        return new MaterialInstance
        {
            Handle = handle.Deserialize<Handle<Material>>(options),
            Overrides = element.TryGetProperty("overrides", out JsonElement overrides)
                ? overrides.Deserialize<MaterialOverrides>(options)
                : default,
        };
    }

    private static void WriteInstance(
        Utf8JsonWriter writer,
        in MaterialInstance instance,
        JsonSerializerOptions options
    )
    {
        if (instance.Overrides.Params.IsEmpty)
        {
            JsonSerializer.Serialize(writer, instance.Handle, options);

            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("handle");
        JsonSerializer.Serialize(writer, instance.Handle, options);
        writer.WritePropertyName("overrides");
        JsonSerializer.Serialize(writer, instance.Overrides, options);
        writer.WriteEndObject();
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

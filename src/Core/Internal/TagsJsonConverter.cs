using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Contexts.ECS;

namespace Magic.Internal;

/// <summary>
/// An entity's tags as JSON: an array of their ids, as a saved world holds them, since a tag is the hash of its name
/// and keeps no name to write. Past <see cref="Tags.Capacity"/>, the rest are left out. A prefab gives an entity its
/// tags by name instead, beside its components, which the gem that reads prefabs turns into these.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The serializer makes it, as Tags' [JsonConverter] names it.")]
internal sealed class TagsJsonConverter : JsonConverter<Tags>
{
    public override Tags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Tags are an array of ids.");
        }

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
            {
                writer.WriteNumberValue(tag.Id);
            }
        }

        writer.WriteEndArray();
    }
}

using Magic.Contexts.Components;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A <see cref="PassList"/> as a JSON array of handles, in order. An inline array is not something System.Text.Json
/// reads by itself, and the order of a list is the point of it, so this is the one converter the assets have. More
/// passes than fit are an error, not dropped: the ones left out would silently not run.
/// </summary>
internal sealed class PassListConverter : JsonConverter<PassList>
{
    public override PassList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("a pass list is an array of handles.");

        PassList list = default;
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == PassList.Capacity)
                throw new JsonException($"a pass list holds at most {PassList.Capacity} passes.");

            list[count++] = JsonSerializer.Deserialize<Handle<Pass>>(ref reader, options);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, PassList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
            JsonSerializer.Serialize(writer, value[i], options);
        writer.WriteEndArray();
    }
}

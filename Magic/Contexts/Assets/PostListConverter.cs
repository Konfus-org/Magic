using Magic.Contexts.Components;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A <see cref="PostList"/> as a JSON array of handles, in order. An inline array is not something System.Text.Json
/// reads by itself, and the order of a list is the point of it, so this is the one converter the assets have. More
/// posts than fit are an error, not dropped: the ones left out would silently not run.
/// </summary>
internal sealed class PostListConverter : JsonConverter<PostList>
{
    public override PostList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("a post list is an array of handles.");

        PostList list = default;
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == PostList.Capacity)
                throw new JsonException($"a post list holds at most {PostList.Capacity} posts.");

            list[count++] = JsonSerializer.Deserialize<Handle<Post>>(ref reader, options);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, PostList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
            JsonSerializer.Serialize(writer, value[i], options);
        writer.WriteEndArray();
    }
}

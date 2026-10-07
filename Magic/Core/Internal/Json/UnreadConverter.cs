using Magic.Contexts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// An <see cref="Unread"/> is the JSON it was read from, kept as it is, and written back the same.
/// </summary>
internal sealed class UnreadConverter : JsonConverter<Unread>
{
    public override Unread Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new Unread(JsonElement.ParseValue(ref reader));
    }

    public override void Write(Utf8JsonWriter writer, Unread value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
            writer.WriteNullValue();
        else
            value.Value.WriteTo(writer);
    }
}

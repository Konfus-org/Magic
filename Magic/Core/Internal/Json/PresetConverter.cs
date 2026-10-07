using Magic.Contexts;
using Magic.Contexts.Assets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// A <c>.preset</c> file: one flat object of <c>"Owner.Property": value</c>, every value kept as it was read until the
/// settings give it its owner's type.
/// </summary>
internal sealed class PresetConverter : JsonConverter<Preset>
{
    public override Preset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a preset is an object of \"Owner.Property\": value.");

        Dictionary<string, Unread> values = new(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString() ?? "";
            reader.Read();
            values[key] = new Unread(JsonElement.ParseValue(ref reader));
        }

        return new Preset { Values = values };
    }

    public override void Write(Utf8JsonWriter writer, Preset value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((string key, Unread setting) in value.Values)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, setting, options);
        }

        writer.WriteEndObject();
    }
}

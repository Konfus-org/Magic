using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Ecs;

/// <summary>
/// Reads and writes a component held as <see cref="IComponent"/> as JSON: the name of its type and its values.
/// </summary>
/// <remarks>
/// The JSON is <c>{ "type": "Transform", "values": { "position": { "x": 0, "y": 1, "z": 0 } } }</c>. The read finds the
/// type in the loaded code, so the JSON outlives the code that wrote it. A type that no code declares fails the read.
/// </remarks>
internal sealed class ComponentJsonConverter : JsonConverter<IComponent>
{
    public override IComponent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement json = document.RootElement;
        if (
            json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("type", out JsonElement named)
            || named.GetString() is not { } name
            || !json.TryGetProperty("values", out JsonElement values)
        )
        {
            throw new JsonException(
                "A component is an object of its type and values: { \"type\": \"Transform\", \"values\": {} }."
            );
        }

        Result<Type> type = Assemblies.Get<IComponent>(name);
        if (!type.Ok)
            throw new JsonException(type.Error);

        return values.Deserialize(type.Value, options) as IComponent
            ?? throw new JsonException($"{name}'s values are null.");
    }

    public override void Write(Utf8JsonWriter writer, IComponent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.GetType().Name);
        writer.WritePropertyName("values");
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
        writer.WriteEndObject();
    }
}

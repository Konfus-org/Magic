using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;

namespace Magic.Internal;

/// <summary>
/// An entity's scripts as JSON: an array of their scripts' handles, <c>[ { "id": 3312 } ]</c>, as a saved world
/// holds them. Past <see cref="Scripts.Capacity"/>, the rest are left out. A prefab gives an entity its scripts with
/// their values instead, in its scripts list, which the gem that reads prefabs turns into these.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The serializer makes it, as Scripts' [JsonConverter] names it.")]
internal sealed class ScriptsJsonConverter : JsonConverter<Scripts>
{
    public override Scripts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Scripts are an array of handles.");
        }

        Scripts scripts = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            scripts.Add(JsonSerializer.Deserialize<Handle<Script>>(ref reader, options));
        }

        return scripts;
    }

    public override void Write(Utf8JsonWriter writer, Scripts value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (Handle<Script> script in value)
        {
            if (script != Handle<Script>.None)
            {
                JsonSerializer.Serialize(writer, script, options);
            }
        }

        writer.WriteEndArray();
    }
}

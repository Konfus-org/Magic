using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Ecs.Components.Effects;

/// <summary>
/// A <see cref="WeatherEffects"/> as JSON: an array of its effects in order, each an object of its fields with its type's
/// name first, <c>[ { "type": "Fog", "density": 0.02 } ]</c>, the type found in the code loaded
/// (<see cref="Assemblies.Get{T}"/>). A type it does not know, one listed twice, or more than it has room for, fails the
/// read. The serializer has no form for an inline array: it writes one as <c>{}</c> and cannot read it back.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as a [JsonConverter] names it."
)]
internal sealed class WeatherEffectsJsonConverter : JsonConverter<WeatherEffects>
{
    private const string TypeKey = "type";

    public override WeatherEffects Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Effects are an array, each naming its type.");

        WeatherEffects effects = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            object effect = ReadEffect(JsonNode.Parse(ref reader), options);
            Type type = effect.GetType();

            if (effects.Count == WeatherEffects.Capacity)
                throw new JsonException($"There are more than the {WeatherEffects.Capacity} effects it holds.");

            foreach (IWeatherEffect held in effects)
            {
                if (held.GetType() == type)
                    throw new JsonException($"{type.Name} is listed twice.");
            }

            try
            {
                effects.Set(effect);
            }
            catch (ArgumentException ex)
            {
                throw new JsonException(ex.Message, ex);
            }
        }

        return effects;
    }

    public override void Write(Utf8JsonWriter writer, WeatherEffects value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (IWeatherEffect effect in value)
        {
            Type type = effect.GetType();
            writer.WriteStartObject();
            writer.WriteString(TypeKey, type.Name);
            if (JsonSerializer.SerializeToNode(effect, type, options) is JsonObject fields)
            {
                foreach ((string name, JsonNode? field) in fields)
                {
                    writer.WritePropertyName(name);
                    if (field is null)
                        writer.WriteNullValue();
                    else
                        field.WriteTo(writer, options);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// One effect: its type found by the name it gives, then read from the rest of its fields.
    /// </summary>
    private static object ReadEffect(JsonNode? node, JsonSerializerOptions options)
    {
        if (
            node is not JsonObject fields
            || fields[TypeKey] is not JsonValue named
            || !named.TryGetValue(out string? name)
        )
        {
            throw new JsonException($"An effect is an object naming its type: {{ \"{TypeKey}\": \"Fog\" }}.");
        }

        Result<Type> type = Assemblies.Get<IWeatherEffect>(name);
        if (!type.Ok)
            throw new JsonException(type.Error);

        if (!type.Value.IsValueType)
            throw new JsonException($"{name} is no struct, as an effect is.");

        fields.Remove(TypeKey);

        return fields.Deserialize(type.Value, options) ?? throw new JsonException($"{name} is null.");
    }
}

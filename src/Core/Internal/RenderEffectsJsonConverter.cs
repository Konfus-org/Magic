using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magic.Contexts.Rendering;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// A <see cref="RenderEffects{T}"/> as JSON: an array of its effects in order, each an object of its fields with its
/// type's name first, <c>[ { "type": "Tonemap", "shader": { "id": 7102 }, "bias": 0 } ]</c>, the type found in the code
/// loaded (<see cref="Assemblies.TryGet{T}"/>). A type it does not know, one listed twice, or more than it has room
/// for, fails the read. The serializer has no form for an inline array: it writes one as <c>{}</c> and cannot read it
/// back.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it, as RenderEffects' [JsonConverter] names it.")]
internal sealed class RenderEffectsJsonConverter : JsonConverterFactory
{
    private const string TypeKey = "type";

    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(RenderEffects<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type converter = typeof(Of<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);

        return Activator.CreateInstance(converter) as JsonConverter
            ?? throw new InvalidOperationException($"{converter.Name} could not be made.");
    }

    private sealed class Of<T> : JsonConverter<RenderEffects<T>>
        where T : IRenderEffect
    {
        public override RenderEffects<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Effects are an array, each naming its type.");

            RenderEffects<T> effects = default;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                object effect = ReadEffect(JsonNode.Parse(ref reader), options);
                Type type = effect.GetType();

                if (effects.Count == RenderEffects<T>.Capacity)
                    throw new JsonException($"There are more than the {RenderEffects<T>.Capacity} effects it holds.");

                foreach (T held in effects)
                {
                    if (held.GetType() == type) throw new JsonException($"{type.Name} is listed twice.");
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

        public override void Write(Utf8JsonWriter writer, RenderEffects<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (T effect in value)
            {
                Type type = effect.GetType();
                writer.WriteStartObject();
                writer.WriteString(TypeKey, type.Name);
                if (JsonSerializer.SerializeToNode(effect, type, options) is JsonObject fields)
                {
                    foreach ((string name, JsonNode? field) in fields)
                    {
                        writer.WritePropertyName(name);
                        if (field is null) writer.WriteNullValue();
                        else field.WriteTo(writer, options);
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
            if (node is not JsonObject fields || fields[TypeKey] is not JsonValue named
                || !named.TryGetValue(out string? name))
            {
                throw new JsonException($"An effect is an object naming its type: {{ \"{TypeKey}\": \"Tonemap\" }}.");
            }

            if (Json.Assemblies is not { } assemblies)
                throw new JsonException($"{name} cannot be found, as no code is loaded to find it in.");

            Result<Type> type = assemblies.TryGet<T>(name);

            if (!type.Ok) throw new JsonException(type.Error);

            if (!type.Value.IsValueType) throw new JsonException($"{name} is no struct, as an effect is.");

            fields.Remove(TypeKey);

            return fields.Deserialize(type.Value, options) ?? throw new JsonException($"{name} is null.");
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Utils;

namespace Magic.Serialization;

/// <summary>
/// The one JSON dialect, for whatever reads a file: camelCase keys matched exactly, enums by their names in snake_case
/// (<c>"clamp"</c>, <c>"masked"</c>), comments and trailing commas allowed, public fields read as properties are, a
/// float that is infinite or not a number written and read as <c>"Infinity"</c>, <c>"-Infinity"</c> or <c>"NaN"</c>,
/// and a key the type does not have, a null where it allows none, or a missing required constructor parameter refused,
/// so a misspelt or broken file fails loudly. Safe from any thread.
/// </summary>
public static class Json
{
    /// <summary>
    /// The dialect, for .NET's serializer itself: <c>JsonSerializer.Deserialize&lt;T&gt;(json, Json.Options)</c>. It
    /// cannot be changed.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateDialect();

    /// <summary>
    /// <paramref name="utf8"/> as a <typeparamref name="T"/>, or why it is not one. A byte order mark, which editors on
    /// Windows may start a file with and JSON does not allow, is skipped.
    /// </summary>
    public static Result<T> Read<T>(ReadOnlySpan<byte> utf8)
    {
        ReadOnlySpan<byte> byteOrderMark = Encoding.UTF8.Preamble;
        if (utf8.StartsWith(byteOrderMark))
            utf8 = utf8[byteOrderMark.Length..];

        try
        {
            return ToResult(JsonSerializer.Deserialize<T>(utf8, Options));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc cref="Read{T}(ReadOnlySpan{byte})"/>
    public static Result<T> Read<T>(JsonElement json)
    {
        try
        {
            return ToResult(json.Deserialize<T>(Options));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="json"/> as a <paramref name="type"/> known only at run time, or why it is not one.
    /// </summary>
    public static Result<object> Read(JsonElement json, Type type)
    {
        try
        {
            return ToResult(json.Deserialize(type, Options));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="value"/>, a <paramref name="type"/> known only at run time, as JSON in the dialect, or why JSON
    /// cannot hold it: a type the serializer does not take, or one that refers to itself.
    /// </summary>
    public static Result<JsonElement> Write(object? value, Type type)
    {
        try
        {
            return Result.Success(JsonSerializer.SerializeToElement(value, type, Options));
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            return Result.Failure(ex.Message);
        }
    }

    private static JsonSerializerOptions CreateDialect()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    /// <summary>
    /// <paramref name="value"/>, as JSON read it, or why it is not one: JSON's <c>null</c> is no value.
    /// </summary>
    private static Result<T> ToResult<T>(T? value) =>
        value is null ? Result.Failure("it is null.") : Result.Success(value);
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// The one JSON dialect for each file that the engine reads, which fails loudly on a misspelt or broken file.
/// </summary>
/// <remarks>
/// Keys are camelCase and match exactly. Enums are their names in snake_case (<c>"clamp"</c>, <c>"masked"</c>).
/// Comments and trailing commas are allowed. Public fields are read as properties are. A property with no setter is
/// left out when it is written. A float that is infinite or not a number is written and read as <c>"Infinity"</c>,
/// <c>"-Infinity"</c> or <c>"NaN"</c>. It refuses a key that the type does not have, a null where the type allows none,
/// and a missing required constructor parameter. Safe from any thread.
/// </remarks>
public static class Json
{
    /// <summary>
    /// The dialect, for .NET's serializer itself: <c>JsonSerializer.Deserialize&lt;T&gt;(json, Json.Options)</c>. It
    /// cannot be changed.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateDialect();

    /// <summary>
    /// The dialect written indented, for a file a person reads and edits. It cannot be changed.
    /// </summary>
    internal static JsonSerializerOptions Indented { get; } = CreateIndented();

    /// <summary>
    /// <paramref name="utf8"/> as a <typeparamref name="T"/>, or why it is not one.
    /// </summary>
    /// <remarks>
    /// It skips a byte order mark, which editors on Windows can put at the start of a file and JSON does not allow.
    /// </remarks>
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
            IgnoreReadOnlyProperties = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    private static JsonSerializerOptions CreateIndented()
    {
        JsonSerializerOptions options = new(Options) { WriteIndented = true };
        options.MakeReadOnly();

        return options;
    }

    /// <summary>
    /// <paramref name="value"/>, as JSON read it, or why it is not one: JSON's <c>null</c> is no value.
    /// </summary>
    private static Result<T> ToResult<T>(T? value) =>
        value is null ? Result.Failure("it is null.") : Result.Success(value);
}

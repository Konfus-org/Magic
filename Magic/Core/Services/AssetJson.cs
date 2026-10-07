using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Utils;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// The one place JSON is read and written: every asset file and sidecar, the project file, presets and settings,
/// chunks, screenshots' world state. camelCase keys, snake_case enum names (<c>clamp_to_edge</c>), fields included so
/// System.Numerics vectors serialise as <c>{x, y, z, w}</c>, comments and trailing commas tolerated, infinities written
/// by name, and what a type only computes (a get-only property) is not written. A <see cref="Handle{T}"/> is
/// <c>{ "id": N }</c>. What JSON cannot say by itself has a converter here (Internal/Json): <see cref="Unread"/>, a
/// <see cref="Contexts.Components.PostList"/>, a <see cref="Project"/> and a <see cref="Preset"/>. A value whose type only its user knows
/// stays <see cref="Unread"/> until that user reads it here. Nothing else names a JSON type but the two readers that
/// walk files themselves, with these options: <see cref="Assets"/> (a sidecar's id, a JSON asset's base chain) and the
/// chunk reader (<see cref="ReaderOptions"/>, <see cref="ReadUnread"/>).
/// </summary>
public static class AssetJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // Camera.Far = +Infinity round-trips as "Infinity"
        Converters =
        {
            new UnreadConverter(),
            new PostListConverter(),
            new ProjectConverter(),
            new PresetConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    /// <summary>
    /// <see cref="Options"/> on one line: for what goes inside something else (a screenshot's text chunk, a setting).
    /// </summary>
    public static JsonSerializerOptions Compact { get; } = new(Options) { WriteIndented = false };

    /// <summary>
    /// What a reader walking JSON bytes itself tolerates: what <see cref="Options"/> does.
    /// </summary>
    public static JsonReaderOptions ReaderOptions => new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The <typeparamref name="T"/> <paramref name="value"/> holds; a failure saying why when it holds none, or another.
    /// </summary>
    public static Result<T> Read<T>(Unread value)
    {
        Result<object> read = Read(value, typeof(T));
        return read is { Ok: true, Payload: T typed } ? Result<T>.Success(typed) : Result<T>.Failure(read.Message);
    }

    /// <summary>
    /// The <paramref name="type"/> <paramref name="value"/> holds; a failure saying why when it holds none, or another.
    /// </summary>
    public static Result<object> Read(Unread value, Type type)
    {
        if (value.IsEmpty)
            return Result<object>.Failure("there is no value.");

        try
        {
            return value.Value.Deserialize(type, Options) is { } read ? Result<object>.Success(read) : Result<object>.Failure("the value is null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return Result<object>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Sets the public properties of <paramref name="target"/> that <paramref name="value"/>'s keys name (ignoring case),
    /// each read as its property's type; a key no property of it can be set by is left alone. For an object made some
    /// other way than by the serializer (a script, made through injection), and by reflection rather than the
    /// serializer's contract for its type: the serializer keeps what it builds for a type, which would keep a reloaded
    /// gem's old assembly loaded.
    /// </summary>
    public static Result Fill(object target, Unread value)
    {
        if (value.Value.ValueKind != JsonValueKind.Object)
            return Result.Failure("the value is not an object.");

        foreach (JsonProperty entry in value.Value.EnumerateObject())
        {
            if (target.GetType().GetProperty(entry.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not { SetMethod.IsPublic: true } property)
                continue;

            Result<object> read = Read(new Unread(entry.Value), property.PropertyType);
            if (read.Failed)
                return Result.Failure($"{entry.Name}: {read.Message}");

            property.SetValue(target, read.Payload);
        }

        return Result.Success();
    }

    /// <summary>
    /// The <typeparamref name="T"/> a file's UTF-8 bytes hold (a byte order mark is skipped).
    /// </summary>
    public static Result<T> Read<T>(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8.WithoutBom(), Options) is { } read ? Result<T>.Success(read) : Result<T>.Failure("the file is null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return Result<T>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="value"/> as an <see cref="Unread"/>, for whoever writes a file of values it does not type (a chunk's
    /// components, a setting).
    /// </summary>
    public static Unread Write<T>(T value)
    {
        return new Unread(JsonSerializer.SerializeToElement(value, Options));
    }

    /// <summary>
    /// <paramref name="value"/>, of <paramref name="type"/>, as an <see cref="Unread"/>.
    /// </summary>
    public static Unread Write(object? value, Type type)
    {
        return new Unread(JsonSerializer.SerializeToElement(value, type, Options));
    }

    /// <summary>
    /// <paramref name="value"/> as a file's UTF-8 bytes.
    /// </summary>
    public static byte[] ToBytes<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    /// <summary>
    /// The value the reader is on, kept as it is for whoever knows its type later; the reader is left at its end.
    /// </summary>
    public static Unread ReadUnread(ref Utf8JsonReader reader)
    {
        return new Unread(JsonElement.ParseValue(ref reader));
    }

    /// <summary>
    /// The <c>"id"</c> of an object value, <c>{ "id": N, ... }</c>, as a handle or a chunk's script entry names an asset;
    /// false for anything else.
    /// </summary>
    public static bool TryGetId(Unread value, out ulong id)
    {
        id = 0;
        return value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("id", out JsonElement number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetUInt64(out id);
    }

    /// <summary>
    /// A pass parameter as a preset or <c>--set</c> writes it: a number, <c>true</c>/<c>false</c>, <c>[x, y, z, w]</c>
    /// (which is also <c>[r, g, b, a]</c>, so it serves a vector or a colour), or the full object. Null for anything else.
    /// </summary>
    public static Param? ParamOf(Unread value)
    {
        JsonElement json = value.Value;
        switch (json.ValueKind)
        {
            case JsonValueKind.Number:
                return Param.Of(json.GetSingle());
            case JsonValueKind.True or JsonValueKind.False:
                return Param.Of(json.GetBoolean());
            case JsonValueKind.Array:
                float[] numbers = [.. json.EnumerateArray().Take(4).Select(item => item.ValueKind == JsonValueKind.Number ? item.GetSingle() : 0f)];
                Array.Resize(ref numbers, 4);
                return new Param { X = numbers[0], Y = numbers[1], Z = numbers[2], W = numbers[3], R = numbers[0], G = numbers[1], B = numbers[2], A = numbers[3] };
            case JsonValueKind.Object:
                return Read<Param>(value) is { Ok: true } read ? read.Payload : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A value as <c>--set</c> and the console write it: JSON, read as every file is (comments, trailing commas), or
    /// else the text itself (what an enum or a path wants).
    /// </summary>
    public static Unread Parse(string text)
    {
        try
        {
            return new Unread(JsonSerializer.Deserialize<JsonElement>(text, Options));
        }
        catch (JsonException)
        {
            return new Unread(JsonSerializer.SerializeToElement(text, Options));
        }
    }

    /// <summary>
    /// <paramref name="value"/> as <c>--set</c> and the console take it: JSON, on one line.
    /// </summary>
    public static string Format(object? value, Type type)
    {
        return JsonSerializer.Serialize(value, type, Compact);
    }
}

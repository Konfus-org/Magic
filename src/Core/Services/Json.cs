using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The one JSON dialect, for whatever reads a file: camelCase keys matched exactly, enums by their names in snake_case
/// (<c>"clamp"</c>, <c>"masked"</c>), comments and trailing commas allowed, public fields read as properties are (a
/// component's), a float that is infinite or not a number written and read as <c>"Infinity"</c>, <c>"-Infinity"</c> or
/// <c>"NaN"</c>, and a key the type does not have, or a null where it allows none, refused, so a misspelt or broken
/// file fails loudly. Any type reads in it as it is, Core's, a gem's or a game's: .NET's serializer finds its members
/// by reflection, so nothing is declared for it. Safe from any thread.
/// </summary>
public static class Json
{
    /// <summary>
    /// The dialect, for .NET's serializer itself: <c>JsonSerializer.Deserialize&lt;T&gt;(json, Json.Options)</c>. It
    /// cannot be changed.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = MakeDialect();

    /// <summary>
    /// <paramref name="utf8"/> as a <typeparamref name="T"/>, or why it is not one. A byte order mark, which editors on
    /// Windows may start a file with and JSON does not allow, is skipped.
    /// </summary>
    public static Result<T> Read<T>(ReadOnlySpan<byte> utf8)
    {
        ReadOnlySpan<byte> byteOrderMark = Encoding.UTF8.Preamble;
        if (utf8.StartsWith(byteOrderMark)) utf8 = utf8[byteOrderMark.Length..];

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(utf8, Options);
        }
        catch (JsonException ex)
        {
            return Result.Failure<T>(ex.Message);
        }

        return value is null ? Result.Failure<T>("it is null.") : Result.Success(value);
    }

    /// <inheritdoc cref="Read{T}(ReadOnlySpan{byte})"/>
    public static Result<T> Read<T>(JsonElement json)
    {
        T? value;
        try
        {
            value = json.Deserialize<T>(Options);
        }
        catch (JsonException ex)
        {
            return Result.Failure<T>(ex.Message);
        }

        return value is null ? Result.Failure<T>("it is null.") : Result.Success(value);
    }

    /// <summary>
    /// <paramref name="json"/> as a <paramref name="type"/> known only at run time, as a saved world holds a component
    /// by its type's name, or why it is not one.
    /// </summary>
    public static Result<object> Read(JsonElement json, Type type)
    {
        object? value;
        try
        {
            value = json.Deserialize(type, Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return Result.Failure<object>(ex.Message);
        }

        return value is null ? Result.Failure<object>("it is null.") : Result.Success(value);
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
            return Result.Failure<JsonElement>(ex.Message);
        }
    }

    private static JsonSerializerOptions MakeDialect()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WithoutAssetHandle } },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Leaves an asset's <see cref="IAsset{TSelf}.Handle"/> out of its JSON: which asset it is, its provider gives it,
    /// so a file never says, and one that tries has a key its type does not.
    /// </summary>
    private static void WithoutAssetHandle(JsonTypeInfo type)
    {
        if (!IsAsset(type.Type)) return;

        for (int i = type.Properties.Count - 1; i >= 0; i--)
        {
            if (type.Properties[i].AttributeProvider is PropertyInfo { Name: nameof(IAsset<Domain>.Handle) })
            {
                type.Properties.RemoveAt(i);
            }
        }
    }

    private static bool IsAsset(Type type)
    {
        foreach (Type contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAsset<>)) return true;
        }

        return false;
    }
}

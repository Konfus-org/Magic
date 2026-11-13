using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magic.Files;

namespace Magic.Utils;

/// <summary>
/// A value as the console types it: JSON, or bare words for text, a choice, the parts of a value or a list.
/// </summary>
/// <remarks>
/// Text or a choice is one bare word. A value with several parts is one word per part, in their order (<c>1 2 3</c>
/// for a position, <c>2560 1440</c> for a size). A list is one word per element (<c>Bloom Vignette</c>).
/// </remarks>
internal static class ValueText
{
    /// <summary>
    /// <paramref name="words"/> as a <paramref name="type"/>: one value, or one per part of a value that has several.
    /// </summary>
    public static Result<object> Parse(Type type, ReadOnlySpan<string> words)
    {
        if (words.IsEmpty)
            return Result.Failure($"it needs a {type.Name}.");

        // A list typed whole as JSON reads as one value; else each word is an element.
        if (IsList(type) && !(words.Length == 1 && IsJson(words[0])))
            return ParseOne(type, $"[{string.Join(',', words.ToArray().Select(ToJson))}]");

        if (words.Length == 1)
            return ParseOne(type, words[0]);

        MemberInfo[] parts = GetParts(type);
        if (parts.Length != words.Length)
        {
            return Result.Failure(
                $"a {type.Name} takes one value in JSON, or {parts.Length} for {string.Join(", ", parts.Select(part => part.Name))}, not {words.Length}."
            );
        }

        if (Activator.CreateInstance(type) is not { } made)
            return Result.Failure($"a {type.Name} cannot be made from its parts.");

        for (int i = 0; i < parts.Length; i++)
        {
            Result<object> part = ParseOne(GetValueType(parts[i]), words[i]);
            if (!part.Ok)
                return Result.Failure($"{parts[i].Name}: {part.Error}");

            SetMember(parts[i], made, part.Value);
        }

        return Result.Success(made);
    }

    /// <summary>
    /// <paramref name="text"/> as the words <see cref="Parse"/> takes: one JSON object, array or string whole, anything
    /// else split at its spaces.
    /// </summary>
    public static string[] Split(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length > 0 && "{[\"".Contains(trimmed[0], StringComparison.Ordinal))
            return [trimmed];

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// <paramref name="value"/> written the way <see cref="Parse"/> reads it: a word per part of a value with several,
    /// otherwise JSON, with text or a choice bare when it is one word.
    /// </summary>
    public static string Format(object value)
    {
        MemberInfo[] parts = GetParts(value.GetType());
        if (parts.Length < 2)
            return FormatOne(value);

        return string.Join(' ', parts.Select(part => FormatOne(GetValue(part, value))));
    }

    public static void SetMember(MemberInfo member, object owner, object? value)
    {
        if (member is FieldInfo field)
            field.SetValue(owner, value);
        else
            ((PropertyInfo)member).SetValue(owner, value);
    }

    public static Type GetValueType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    public static object? GetValue(MemberInfo member, object owner) =>
        member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);

    /// <summary>
    /// <paramref name="text"/> as a <paramref name="type"/>, read as JSON; a bare word is read as JSON's text, for a
    /// string or a choice.
    /// </summary>
    private static Result<object> ParseOne(Type type, string text)
    {
        string json = ToJson(text);
        Result<JsonElement> element = Json.Read<JsonElement>(Encoding.UTF8.GetBytes(json));
        if (!element.Ok)
            return Result.Failure($"'{text}' is not JSON: {element.Error}");

        Result<object> read = Json.Read(element.Value, type);
        if (!read.Ok)
            return Result.Failure($"'{text}' is not a {type.Name}: {read.Error}");

        // A choice also reads from a number, which may name none of its values.
        if (type.IsEnum && !Enum.IsDefined(type, read.Value))
            return Result.Failure($"{text} is none of {string.Join(", ", Enum.GetNames(type))}.");

        return read;
    }

    /// <summary>
    /// <paramref name="value"/> as JSON, or the bare word it is when that reads back as the same text.
    /// </summary>
    private static string FormatOne(object? value)
    {
        Result<JsonElement> json = Json.Write(value, value?.GetType() ?? typeof(object));
        if (!json.Ok)
            return value?.ToString() ?? "null";

        // Text or a choice is written bare when it reads back as the same text.
        if (
            json.Value.ValueKind == JsonValueKind.String
            && json.Value.GetString() is { Length: > 0 } text
            && !IsJson(text)
            && !text.Contains(' ', StringComparison.Ordinal)
        )
        {
            return text;
        }

        return json.Value.GetRawText();
    }

    /// <summary>
    /// <paramref name="text"/> as it is when it is JSON, else as JSON's text.
    /// </summary>
    private static string ToJson(string text) => IsJson(text) ? text : JsonSerializer.Serialize(text);

    /// <summary>
    /// Whether a <paramref name="type"/> is a list of values, which text is not.
    /// </summary>
    private static bool IsList(Type type) =>
        type != typeof(string) && type.IsAssignableTo(typeof(System.Collections.IEnumerable));

    /// <summary>
    /// Whether <paramref name="text"/> is JSON, as a value typed bare is not: a number, a literal, a string, an object
    /// or an array.
    /// </summary>
    private static bool IsJson(string text) =>
        text is "true" or "false" or "null"
        || (text.Length > 0 && "-+.0123456789{[\"".Contains(text[0], StringComparison.Ordinal));

    /// <summary>
    /// What a value of <paramref name="type"/> is made of, in their order: its public fields, or its settable
    /// properties when it has no fields.
    /// </summary>
    private static MemberInfo[] GetParts(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (fields.Length > 0)
            return fields;

        return
        [
            .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0),
        ];
    }
}

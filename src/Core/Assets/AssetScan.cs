using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Magic.Assets;

/// <summary>
/// Finds what an asset depends on without loading anything else: the ids of the <see cref="Handle{T}"/>s it holds,
/// however deep in its properties and collections.
/// </summary>
internal static class AssetScan
{
    /// <summary>
    /// The ids of every <see cref="Handle{T}"/> in <paramref name="asset"/>, but none's, each once.
    /// </summary>
    public static ImmutableArray<ulong> FindHandles(object asset)
    {
        HashSet<ulong> found = [];
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        Visit(asset, found, seen);

        return [.. found];
    }

    /// <summary>
    /// Adds the ids of the handles in <paramref name="value"/> to <paramref name="found"/>: it, when it is one, else
    /// what its collection holds, else its public properties.
    /// </summary>
    private static void Visit(object? value, HashSet<ulong> found, HashSet<object> seen)
    {
        if (value is null or string or JsonNode)
            return;

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type.IsPointer)
            return;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Handle<>))
        {
            if (type.GetProperty(nameof(Handle<>.Id))?.GetValue(value) is ulong id and not 0)
                found.Add(id);

            return;
        }

        // A class is visited once, so one that refers back to itself ends.
        if (!type.IsValueType && !seen.Add(value))
            return;

        if (value is IEnumerable items)
        {
            foreach (object? item in items)
            {
                Visit(item is DictionaryEntry entry ? entry.Value : item, found, seen);
            }

            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && !property.PropertyType.IsByRefLike)
                Visit(property.GetValue(value), found, seen);
        }
    }
}

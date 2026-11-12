using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using Magic.Ecs.Components;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Finds what an asset depends on without loading anything else: the ids of the <see cref="Handle{T}"/>s it holds,
/// however deep in its properties and collections.
/// </summary>
internal static class AssetScan
{
    /// <summary>
    /// Whether each struct holds anything a handle could be in, worked out once.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, bool> _canHoldHandle = new();

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

    private static Type? GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        foreach (Type contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return contract.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> can hold a handle: anything that may refer to other objects can.
    /// </summary>
    private static bool CanHoldHandle(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(decimal) || type == typeof(string))
            return false;

        if (!type.IsValueType || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Handle<>)))
            return true;

        // A renderer's material set holds its slots in the process's store, behind an id, and shows them as a property.
        if (type == typeof(MaterialSet))
            return true;

        return _canHoldHandle.GetOrAdd(
            type,
            static found =>
                Array.Exists(
                    found.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                    field => CanHoldHandle(field.FieldType)
                )
        );
    }

    /// <summary>
    /// Adds the ids of the handles in <paramref name="value"/> to <paramref name="found"/>: it, when it is one, else
    /// what its collection holds, else its public fields, as a component's are, and properties.
    /// </summary>
    private static void Visit(object? value, HashSet<ulong> found, HashSet<object> seen)
    {
        if (value is null or string or JsonNode)
            return;

        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Handle<>))
        {
            if (type.GetProperty(nameof(Handle<>.Id))?.GetValue(value) is ulong id and not 0)
                found.Add(id);

            return;
        }

        if (type.IsValueType && !CanHoldHandle(type))
            return;

        // A class is visited once, so one that refers back to itself ends.
        if (!type.IsValueType && !seen.Add(value))
            return;

        if (value is IEnumerable items)
        {
            // An image's pixels or a mesh's vertices can't hold one, and would be boxed one by one to find that out.
            if (GetElementType(type) is { } element && !CanHoldHandle(element))
                return;

            foreach (object? item in items)
            {
                Visit(item is DictionaryEntry entry ? entry.Value : item, found, seen);
            }

            return;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.FieldType.IsByRefLike && !field.FieldType.IsPointer)
                Visit(field.GetValue(value), found, seen);
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && !property.PropertyType.IsByRefLike)
                Visit(property.GetValue(value), found, seen);
        }
    }
}

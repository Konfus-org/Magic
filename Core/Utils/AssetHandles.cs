using Magic.Contexts;
using Magic.Contexts.Assets;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Magic.Utils;

/// <summary>
/// Finds the assets an object names: every valid <see cref="Handle{T}"/> of an <see cref="Asset"/> type in its public
/// properties and fields, the ones JSON reads and writes, through nested structs and classes, arrays, lists and
/// dictionaries. So an asset or a component declares its dependencies by having the handles, and nothing is written by
/// hand. How to walk a type is worked out once and kept; a type that cannot hold a handle (a mesh's vertices) is never
/// walked at all. A type that contains itself is not followed round, and <c>System</c> types other than collections
/// are not looked into.
/// </summary>
public static class AssetHandles
{
    private static readonly ConcurrentDictionary<Type, Plan> Plans = new();

    /// <summary>Adds what <paramref name="holder"/> names to <paramref name="found"/>, as asset type and id; the same one may be added twice.</summary>
    public static void Find(object holder, List<(Type Type, ulong Id)> found)
    {
        Plan plan = PlanOf(holder.GetType(), []);
        if (plan.Holds)
            Walk(holder, plan, found);
    }

    /// <summary>Drops what was worked out, so no type of an unloaded gem is kept alive.</summary>
    public static void Forget()
    {
        Plans.Clear();
    }

    private static void Walk(object value, Plan plan, List<(Type Type, ulong Id)> found)
    {
        if (plan.Asset is not null)
        {
            ulong id = (ulong)plan.Id!.GetValue(value)!;
            if (id != 0)
                found.Add((plan.Asset, id));

            return;
        }

        if (plan.Element is not null)
        {
            foreach (object? item in (IEnumerable)value)
            {
                if (item is not null)
                    Walk(item, plan.Element, found);
            }

            return;
        }

        foreach ((Func<object, object?> get, Plan member) in plan.Members)
        {
            if (get(value) is { } inner)
                Walk(inner, member, found);
        }
    }

    /// <summary>How to walk <paramref name="type"/>; <see cref="Plan.None"/> when it cannot hold a handle or is already being worked out further up.</summary>
    private static Plan PlanOf(Type type, HashSet<Type> building)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Plans.TryGetValue(type, out Plan? known))
            return known;

        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || type == typeof(object) || !building.Add(type))
            return Plan.None;

        Plan plan = Build(type, building);
        building.Remove(type);
        if (building.Count == 0)
            Plans[type] = plan; // only a whole answer is kept: one cut short by a type containing itself is not

        return plan;
    }

    private static Plan Build(Type type, HashSet<Type> building)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Handle<>))
        {
            Type asset = type.GetGenericArguments()[0];
            return typeof(Asset).IsAssignableFrom(asset) ? new Plan { Asset = asset, Id = type.GetProperty(nameof(Handle<Asset>.Id)) } : Plan.None;
        }

        if (ElementOf(type) is { } element)
        {
            Plan each = PlanOf(element, building);
            return each.Holds ? new Plan { Element = each } : Plan.None;
        }

        bool pair = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        if (!pair && type.Namespace?.StartsWith(nameof(System), StringComparison.Ordinal) == true)
            return Plan.None;

        List<(Func<object, object?>, Plan)> members = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0 || property.IsDefined(typeof(JsonIgnoreAttribute)))
                continue;

            Plan member = PlanOf(property.PropertyType, building);
            if (member.Holds)
                members.Add((property.GetValue, member));
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsDefined(typeof(JsonIgnoreAttribute)))
                continue;

            Plan member = PlanOf(field.FieldType, building);
            if (member.Holds)
                members.Add((field.GetValue, member));
        }

        return members.Count > 0 ? new Plan { Members = [.. members] } : Plan.None;
    }

    /// <summary>What a collection holds: an array's element, or the T of the IEnumerable&lt;T&gt; it is (a dictionary's key and value pair).</summary>
    private static Type? ElementOf(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        foreach (Type candidate in type.IsInterface ? [type, .. type.GetInterfaces()] : type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>One type's walk: it is a handle (<see cref="Asset"/>), a collection (<see cref="Element"/>), or has <see cref="Members"/> that lead to one.</summary>
    private sealed class Plan
    {
        public static Plan None { get; } = new();

        public Type? Asset { get; init; }

        public PropertyInfo? Id { get; init; }

        public Plan? Element { get; init; }

        public (Func<object, object?> Get, Plan Plan)[] Members { get; init; } = [];

        public bool Holds => this != None;
    }
}

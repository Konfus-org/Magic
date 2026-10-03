using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Magic.Extensions;

public static class ObjectExtensions
{
    extension(object holder)
    {
        /// <summary>
        /// Every value of <typeparamref name="T"/> the object holds: in its public properties and fields, the ones
        /// JSON reads and writes, through nested structs and classes, arrays, lists and dictionaries. A generic
        /// <typeparamref name="T"/> matches through its arguments, so <c>Handle&lt;Asset&gt;</c> yields every
        /// <c>Handle&lt;Model&gt;</c>, <c>Handle&lt;Texture&gt;</c> and so on; <paramref name="strict"/> yields
        /// <typeparamref name="T"/> exactly. So an asset or a component declares what it depends on by having the
        /// handles, and nothing is written by hand. How to walk a type is worked out once and kept; a type that
        /// cannot hold a match (a mesh's vertices) is never walked at all. A type that contains itself is not
        /// followed round, and <c>System</c> types other than collections are not looked into.
        /// </summary>
        public IEnumerable<object> ValuesOf<T>(bool strict = false)
        {
            List<object> found = [];
            Plan plan = PlanOf(holder.GetType(), typeof(T), strict, strict ? Plans<T>.Strict : Plans<T>.Loose, []);
            if (plan.Holds)
                Walk(holder, plan, found);

            return found;
        }
    }

    private static void Walk(object value, Plan plan, List<object> found)
    {
        if (plan.Match)
        {
            found.Add(value);
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

    /// <summary>
    /// How to walk <paramref name="type"/>; <see cref="Plan.None"/> when it cannot hold a match or is already being worked out further up.
    /// </summary>
    private static Plan PlanOf(Type type, Type target, bool strict, ConditionalWeakTable<Type, Plan> plans, HashSet<Type> building)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (plans.TryGetValue(type, out Plan? known))
            return known;

        if (Matches(type, target, strict))
            return Plan.Found;

        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || type == typeof(object) || !building.Add(type))
            return Plan.None;

        Plan plan = Build(type, target, strict, plans, building);
        building.Remove(type);
        if (building.Count == 0)
            plans.AddOrUpdate(type, plan); // only a whole answer is kept: one cut short by a type containing itself is not

        return plan;
    }

    private static Plan Build(Type type, Type target, bool strict, ConditionalWeakTable<Type, Plan> plans, HashSet<Type> building)
    {
        if (ElementOf(type) is { } element)
        {
            Plan each = PlanOf(element, target, strict, plans, building);
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

            Plan member = PlanOf(property.PropertyType, target, strict, plans, building);
            if (member.Holds)
                members.Add((property.GetValue, member));
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsDefined(typeof(JsonIgnoreAttribute)))
                continue;

            Plan member = PlanOf(field.FieldType, target, strict, plans, building);
            if (member.Holds)
                members.Add((field.GetValue, member));
        }

        return members.Count > 0 ? new Plan { Members = [.. members] } : Plan.None;
    }

    /// <summary>
    /// Is a value of <paramref name="type"/> one of <paramref name="target"/>? Exactly it when <paramref name="strict"/>;
    /// otherwise anything assignable to it, or the same generic type with each argument matching the target's.
    /// </summary>
    private static bool Matches(Type type, Type target, bool strict)
    {
        if (type == target)
            return true;
        if (strict || target == typeof(object))
            return false;
        if (target.IsAssignableFrom(type))
            return true;
        if (!type.IsGenericType || !target.IsGenericType || type.GetGenericTypeDefinition() != target.GetGenericTypeDefinition())
            return false;

        Type[] arguments = type.GetGenericArguments();
        Type[] wanted = target.GetGenericArguments();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!Matches(arguments[i], wanted[i], strict: false))
                return false;
        }

        return true;
    }

    /// <summary>
    /// What a collection holds: an array's element, or the T of the IEnumerable&lt;T&gt; it is (a dictionary's key and value pair).
    /// </summary>
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

    /// <summary>
    /// The walks worked out for one target type, by the type walked. The keys are held weakly, so a type of an
    /// unloaded gem is not kept alive by having been walked.
    /// </summary>
    private static class Plans<T>
    {
        public static ConditionalWeakTable<Type, Plan> Loose { get; } = [];

        public static ConditionalWeakTable<Type, Plan> Strict { get; } = [];
    }

    /// <summary>
    /// One type's walk: it is a match (<see cref="Match"/>), a collection (<see cref="Element"/>), or has <see cref="Members"/> that lead to one.
    /// </summary>
    private sealed class Plan
    {
        public static Plan None { get; } = new();

        public static Plan Found { get; } = new() { Match = true };

        public bool Match { get; init; }

        public Plan? Element { get; init; }

        public (Func<object, object?> Get, Plan Plan)[] Members { get; init; } = [];

        public bool Holds => this != None;
    }
}

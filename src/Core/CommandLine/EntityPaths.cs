using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// A value of an entity's, named by a path as the console takes it: <c>entity.component.member</c> and on to any depth,
/// such as <c>player.transform.rotation.y</c>. The entity is a tag's name, which is every entity tagged so, or an id. A
/// component or member is its name in any case, or its capitals' initials (<c>fov</c> for <c>FieldOfView</c>).
/// </summary>
internal static class EntityPaths
{
    /// <summary>
    /// The value at <paramref name="path"/> on each entity it names, as a line of JSON after the entity's id, or why
    /// there is none.
    /// </summary>
    public static Result<ImmutableArray<string>> Read(IEcs ecs, string path)
    {
        Result<ImmutableArray<(Entity Entity, IComponent Component)>> found = FindComponents(ecs, path);
        if (!found.Ok)
            return Result.Failure(found.Error);

        ImmutableArray<string>.Builder lines = ImmutableArray.CreateBuilder<string>();
        string[] members = path.Split('.')[2..];
        foreach ((Entity entity, IComponent component) in found.Value)
        {
            Result<(object Value, Type Type)> value = ReadMembers(component, members);
            if (!value.Ok)
                return Result.Failure(value.Error);

            Result<JsonElement> json = Json.Write(value.Value.Value, value.Value.Type);
            if (!json.Ok)
                return Result.Failure(json.Error);

            lines.Add($"entity {entity.Id}: {json.Value.GetRawText()}");
        }

        return Result.Success(lines.ToImmutable());
    }

    /// <summary>
    /// Sets the value at <paramref name="path"/> on each entity it names that has the component, to
    /// <paramref name="values"/>: one in JSON, or a bare word for text or a choice, or one per field of a value with
    /// several, in their order (<c>1 2 3</c> for a position). How many it set, or why none.
    /// </summary>
    public static Result<int> Write(IEcs ecs, string path, ReadOnlySpan<string> values)
    {
        Result<ImmutableArray<(Entity Entity, IComponent Component)>> found = FindComponents(ecs, path);
        if (!found.Ok)
            return Result.Failure(found.Error);

        string[] members = path.Split('.')[2..];
        if (members.Length == 0)
            return Result.Failure($"{path} names a component; name one of its members to set.");

        foreach ((Entity entity, IComponent component) in found.Value)
        {
            Result written = WriteMembers(component, members, values);
            if (!written.Ok)
                return Result.Failure(written.Error);

            ecs.Set(entity, [component]);
        }

        return Result.Success(found.Value.Length);
    }

    /// <summary>
    /// The component <paramref name="path"/> names on each entity it names that has one, boxed copies to read or write.
    /// </summary>
    private static Result<ImmutableArray<(Entity Entity, IComponent Component)>> FindComponents(IEcs ecs, string path)
    {
        string[] parts = path.Split('.');
        if (parts.Length < 2 || parts[0].Length == 0)
            return Result.Failure($"{path} is not entity.component, then its members.");

        ImmutableArray<Entity> entities = FindEntities(ecs, parts[0]);
        if (entities.IsEmpty)
            return Result.Failure($"no entity is tagged or numbered {parts[0]}.");

        ImmutableArray<(Entity, IComponent)>.Builder found = ImmutableArray.CreateBuilder<(Entity, IComponent)>();
        foreach (Entity entity in entities)
        {
            if (FindComponent(ecs.GetComponents(entity), parts[1]) is { } component)
                found.Add((entity, component));
        }

        return found.Count == 0
            ? Result.Failure($"no entity {parts[0]} names has a {parts[1]}.")
            : Result.Success(found.ToImmutable());
    }

    /// <summary>
    /// The entity numbered <paramref name="selector"/>, or every entity tagged it.
    /// </summary>
    private static ImmutableArray<Entity> FindEntities(IEcs ecs, string selector)
    {
        if (int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            return FindEntity(ecs, id);

        ImmutableArray<Entity>.Builder found = ImmutableArray.CreateBuilder<Entity>();
        Tag tag = Tag.From(selector);
        foreach (EcsChunk<Tags> chunk in ecs.Query<Tags>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Has(tag))
                    found.Add(chunk.Entities[i]);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// The alive entity with <paramref name="id"/>, or none: only one holds an id at a time.
    /// </summary>
    private static ImmutableArray<Entity> FindEntity(IEcs ecs, int id)
    {
        foreach (EcsChunk chunk in ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (entity.Id == id)
                    return [entity];
            }
        }

        return [];
    }

    private static IComponent? FindComponent(IComponent[] components, string name)
    {
        int index = FindName([.. components.Select(component => component.GetType().Name)], name);

        return index < 0 ? null : components[index];
    }

    /// <summary>
    /// The public field or property of <paramref name="type"/> that <paramref name="name"/> names.
    /// </summary>
    private static MemberInfo? FindMember(Type type, string name)
    {
        MemberInfo[] members = [.. type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Where(IsValue)];
        int index = FindName([.. members.Select(member => member.Name)], name);

        return index < 0 ? null : members[index];
    }

    /// <summary>
    /// Where in <paramref name="names"/> the one <paramref name="name"/> names is: by its name in any case first, then by
    /// its initials; -1 for none.
    /// </summary>
    private static int FindName(string[] names, string name)
    {
        int index = Array.FindIndex(names, named => string.Equals(named, name, StringComparison.OrdinalIgnoreCase));

        return index >= 0
            ? index
            : Array.FindIndex(
                names,
                named => string.Equals(GetInitials(named), name, StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// The value at <paramref name="members"/> under <paramref name="owner"/>, and its type; the owner itself for none.
    /// </summary>
    private static Result<(object Value, Type Type)> ReadMembers(object owner, ReadOnlySpan<string> members)
    {
        if (members.IsEmpty)
            return Result.Success((owner, owner.GetType()));

        if (FindMember(owner.GetType(), members[0]) is not { } member)
            return Result.Failure($"{owner.GetType().Name} has no {members[0]}.");

        if (GetValue(member, owner) is not { } value)
            return Result.Failure($"{owner.GetType().Name}.{member.Name} is null.");

        return ReadMembers(value, members[1..]);
    }

    /// <summary>
    /// Writes <paramref name="values"/> at <paramref name="members"/> under <paramref name="owner"/>, a box, which it
    /// changes: each value type on the way is a copy, written into the copy above it once changed.
    /// </summary>
    private static Result WriteMembers(object owner, ReadOnlySpan<string> members, ReadOnlySpan<string> values)
    {
        if (FindMember(owner.GetType(), members[0]) is not { } member)
            return Result.Failure($"{owner.GetType().Name} has no {members[0]}.");

        if (member is PropertyInfo { CanWrite: false })
            return Result.Failure($"{owner.GetType().Name}.{member.Name} follows from other values; set those.");

        if (members.Length == 1)
        {
            Result<object> parsed = ParseValue(GetValueType(member), values);
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            SetValue(member, owner, parsed.Value);

            return Result.Success();
        }

        if (GetValue(member, owner) is not { } child)
            return Result.Failure($"{owner.GetType().Name}.{member.Name} is null.");

        Result written = WriteMembers(child, members[1..], values);
        if (!written.Ok)
            return written;

        SetValue(member, owner, child);

        return Result.Success();
    }

    /// <summary>
    /// <paramref name="values"/> as a <paramref name="type"/>: one value, or one per field of a value that has several.
    /// </summary>
    private static Result<object> ParseValue(Type type, ReadOnlySpan<string> values)
    {
        if (values.IsEmpty)
            return Result.Failure($"set needs a {type.Name} after the path.");

        if (values.Length == 1)
            return ParseOne(type, values[0]);

        MemberInfo[] fields = GetSettableValues(type);
        if (fields.Length != values.Length)
        {
            return Result.Failure(
                $"a {type.Name} takes one value in JSON, or {fields.Length} for {string.Join(", ", fields.Select(field => field.Name))}, not {values.Length}."
            );
        }

        if (Activator.CreateInstance(type) is not { } made)
            return Result.Failure($"a {type.Name} cannot be made from its parts.");

        for (int i = 0; i < fields.Length; i++)
        {
            Result<object> part = ParseOne(GetValueType(fields[i]), values[i]);
            if (!part.Ok)
                return Result.Failure($"{fields[i].Name}: {part.Error}");

            SetValue(fields[i], made, part.Value);
        }

        return Result.Success(made);
    }

    /// <summary>
    /// <paramref name="text"/> as a <paramref name="type"/>, read as JSON; a bare word is read as JSON's text, for a
    /// string or a choice.
    /// </summary>
    private static Result<object> ParseOne(Type type, string text)
    {
        bool isJson =
            text is "true" or "false" or "null"
            || (text.Length > 0 && "-+.0123456789{[\"".Contains(text[0], StringComparison.Ordinal));
        string json = isJson ? text : JsonSerializer.Serialize(text);
        Result<JsonElement> element = Json.Read<JsonElement>(Encoding.UTF8.GetBytes(json));
        if (!element.Ok)
            return Result.Failure($"'{text}' is not JSON: {element.Error}");

        Result<object> read = Json.Read(element.Value, type);

        return read.Ok ? read : Result.Failure($"'{text}' is not a {type.Name}: {read.Error}");
    }

    /// <summary>
    /// What a value of <paramref name="type"/> is made of, in their order: its public fields, or its settable properties
    /// when it has no fields.
    /// </summary>
    private static MemberInfo[] GetSettableValues(Type type)
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

    /// <summary>
    /// The first letter of <paramref name="name"/> and each capital after it.
    /// </summary>
    private static string GetInitials(string name)
    {
        StringBuilder initials = new();
        for (int i = 0; i < name.Length; i++)
        {
            if (i == 0 || char.IsUpper(name[i]))
                initials.Append(name[i]);
        }

        return initials.ToString();
    }

    private static bool IsValue(MemberInfo member) =>
        member is FieldInfo
        || (member is PropertyInfo { CanRead: true } property && property.GetIndexParameters().Length == 0);

    private static Type GetValueType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static object? GetValue(MemberInfo member, object owner) =>
        member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);

    private static void SetValue(MemberInfo member, object owner, object? value)
    {
        if (member is FieldInfo field)
            field.SetValue(owner, value);
        else
            ((PropertyInfo)member).SetValue(owner, value);
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// A value of an entity's, named by a path as the console takes it: <c>entity.component.member</c> and on to any depth,
/// such as <c>player.transform.rotation.y</c>. The entity is a tag's name, which is every entity tagged so, or an id. A
/// component or member is its name in any case, or its capitals' initials (<c>fov</c> for <c>FieldOfView</c>). The console's
/// get reads one; its set writes one.
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
    /// Writes <paramref name="values"/> at <paramref name="path"/> on each entity it names that has the component: how
    /// many it wrote, or why none.
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
            IComponent[] components = ecs.GetComponents(entity);
            int index = FindName([.. components.Select(component => component.GetType().Name)], parts[1]);
            if (index >= 0)
                found.Add((entity, components[index]));
        }

        return found.Count == 0
            ? Result.Failure($"no entity {parts[0]} names has a {parts[1]}.")
            : Result.Success(found.ToImmutable());
    }

    /// <summary>
    /// The public field or property of <paramref name="type"/> that <paramref name="name"/> names.
    /// </summary>
    private static MemberInfo? FindMember(Type type, string name)
    {
        MemberInfo[] members =
        [
            .. type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(member =>
                    member is FieldInfo
                    || (member is PropertyInfo { CanRead: true } property && property.GetIndexParameters().Length == 0)
                ),
        ];
        int index = FindName([.. members.Select(member => member.Name)], name);

        return index < 0 ? null : members[index];
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

    /// <summary>
    /// Where in <paramref name="names"/> the one <paramref name="name"/> names is: by its name in any case first, then by
    /// its initials, its first letter and each capital after it; -1 for none.
    /// </summary>
    private static int FindName(string[] names, string name)
    {
        int index = Array.FindIndex(names, named => string.Equals(named, name, StringComparison.OrdinalIgnoreCase));

        return index >= 0
            ? index
            : Array.FindIndex(
                names,
                named =>
                    string.Equals(
                        string.Concat(named.Where((letter, i) => i == 0 || char.IsUpper(letter))),
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
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

        if (ValueText.GetValue(member, owner) is not { } value)
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
            Result<object> parsed = ValueText.Parse(ValueText.GetValueType(member), values);
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            ValueText.SetMember(member, owner, parsed.Value);

            return Result.Success();
        }

        if (ValueText.GetValue(member, owner) is not { } child)
            return Result.Failure($"{owner.GetType().Name}.{member.Name} is null.");

        Result written = WriteMembers(child, members[1..], values);
        if (!written.Ok)
            return written;

        ValueText.SetMember(member, owner, child);

        return Result.Success();
    }
}

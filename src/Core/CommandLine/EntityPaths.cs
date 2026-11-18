using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// Reads and writes a value of an entity, named by a console path such as <c>player.transform.rotation.y</c>.
/// </summary>
/// <remarks>
/// A path is <c>entity.component.member</c>, to any depth, and <c>entity.component</c> is the whole component. The
/// entity is an id, or the name of a tag, which names each entity with that tag. A component or member is its name in
/// any case, or the initials of its capitals (<c>fov</c> for <c>FieldOfView</c>). The query command of the console
/// reads one, and its set command writes one.
/// </remarks>
internal static class EntityPaths
{
    /// <summary>
    /// The value at <paramref name="path"/> on each entity it names, each as the entity's id and the value in JSON, or
    /// why there is none.
    /// </summary>
    public static Result<JsonArray> Read(IEcs ecs, string path)
    {
        Result<ImmutableArray<(Entity Entity, IComponent Component)>> found = FindComponents(ecs, path);
        if (!found.Ok)
            return Result.Failure(found.Error);

        JsonArray values = [];
        string[] members = path.Split('.')[2..];
        foreach ((Entity entity, IComponent component) in found.Value)
        {
            Result<(object Value, Type Type)> value = ReadMembers(component, component.GetType(), members);
            if (!value.Ok)
                return Result.Failure(value.Error);

            Result<JsonElement> json = Json.Write(value.Value.Value, value.Value.Type);
            if (!json.Ok)
                return Result.Failure(json.Error);

            values.Add(new JsonObject { ["entity"] = entity.Id, ["value"] = JsonNode.Parse(json.Value.GetRawText()) });
        }

        return Result.Success(values);
    }

    /// <summary>
    /// Writes <paramref name="values"/> at <paramref name="path"/> on each entity that it names and that has the
    /// component.
    /// </summary>
    /// <remarks>
    /// A path that names a component and no member of it writes the whole component, as the JSON that
    /// <see cref="Read"/> prints. It returns how many it wrote, or why it wrote none.
    /// </remarks>
    public static Result<int> Write(IEcs ecs, string path, ReadOnlySpan<string> values)
    {
        Result<ImmutableArray<(Entity Entity, IComponent Component)>> found = FindComponents(ecs, path);
        if (!found.Ok)
            return Result.Failure(found.Error);

        string[] members = path.Split('.')[2..];
        foreach ((Entity entity, IComponent held) in found.Value)
        {
            IComponent component = held;

            // A path that names no member names the whole component: the values give it as the JSON that query prints.
            if (members.Length == 0)
            {
                Result<object> whole = ValueText.Parse(held.GetType(), values);
                if (!whole.Ok)
                    return Result.Failure($"{whole.Error} Put the component's JSON in single quotes.");

                component = (IComponent)whole.Value;
            }
            else
            {
                Result written = WriteMembers(held, held.GetType(), members, values);
                if (!written.Ok)
                    return Result.Failure(written.Error);
            }

            ecs.Set(entity, [component]);
        }

        return Result.Success(found.Value.Length);
    }

    /// <summary>
    /// The public field, or the property with a public getter, of <paramref name="type"/> that
    /// <paramref name="name"/> names.
    /// </summary>
    public static MemberInfo? FindMember(Type type, string name)
    {
        MemberInfo[] members =
        [
            .. type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(member =>
                    member is FieldInfo
                    || (
                        member is PropertyInfo { GetMethod.IsPublic: true } property
                        && property.GetIndexParameters().Length == 0
                    )
                ),
        ];
        int index = FindName([.. members.Select(member => member.Name)], name);

        return index < 0 ? null : members[index];
    }

    /// <summary>
    /// Whether <paramref name="property"/> has a public setter, which is the only setter that the console writes
    /// through.
    /// </summary>
    public static bool IsSettable(PropertyInfo property) => property.SetMethod is { IsPublic: true };

    /// <summary>
    /// The value at <paramref name="members"/> under <paramref name="owner"/>, and its type; the owner itself for none.
    /// </summary>
    /// <remarks>
    /// <paramref name="type"/> is the type whose members the owner shows: its own, or an interface of it.
    /// </remarks>
    public static Result<(object Value, Type Type)> ReadMembers(object owner, Type type, ReadOnlySpan<string> members)
    {
        if (members.IsEmpty)
            return Result.Success((owner, type));

        if (FindMember(type, members[0]) is not { } member)
            return Result.Failure($"{type.Name} has no {members[0]}.");

        if (ValueText.GetValue(member, owner) is not { } value)
            return Result.Failure($"{type.Name}.{member.Name} is null.");

        return ReadMembers(value, value.GetType(), members[1..]);
    }

    /// <summary>
    /// Writes <paramref name="values"/> at <paramref name="members"/> under <paramref name="owner"/>, a box that it
    /// changes.
    /// </summary>
    /// <remarks>
    /// Each value type on the way is a copy, which it writes into the copy above it after the change.
    /// <paramref name="type"/> is the type whose members the owner shows: its own, or an interface of it.
    /// </remarks>
    public static Result WriteMembers(
        object owner,
        Type type,
        ReadOnlySpan<string> members,
        ReadOnlySpan<string> values
    )
    {
        if (FindMember(type, members[0]) is not { } member)
            return Result.Failure($"{type.Name} has no {members[0]}.");

        // A setter that is not public is the code's own, which the console must not reach.
        if (member is PropertyInfo property && !IsSettable(property))
            return Result.Failure($"{type.Name}.{member.Name} follows from other values; set those.");

        if (members.Length == 1)
        {
            Result<object> parsed = ValueText.Parse(ValueText.GetValueType(member), values);
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            ValueText.SetMember(member, owner, parsed.Value);

            return Result.Success();
        }

        if (ValueText.GetValue(member, owner) is not { } child)
            return Result.Failure($"{type.Name}.{member.Name} is null.");

        Result written = WriteMembers(child, child.GetType(), members[1..], values);
        if (!written.Ok)
            return written;

        ValueText.SetMember(member, owner, child);

        return Result.Success();
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
    /// Returns the index in <paramref name="names"/> of the name that <paramref name="name"/> names, or -1 for none.
    /// </summary>
    /// <remarks>
    /// It matches the name in any case first, then the initials: the first letter and each capital after it.
    /// </remarks>
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
}

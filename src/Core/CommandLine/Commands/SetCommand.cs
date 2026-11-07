using System.Collections.Immutable;
using System.CommandLine;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>set</c>: <c>Key=value</c> handed to the gem the key names; or an entity's value, named by a path as
/// <see cref="EntityPaths"/> reads it, to one value in JSON, a bare word for text or a choice, or one per field of a
/// value with several, in their order (<c>1 2 3</c> for a position).
/// </summary>
internal static class SetCommand
{
    public static Command Create(App app, EventCoordinator events, World world)
    {
        Argument<string> setting = new("setting")
        {
            Description =
                "Key=value, or the path to an entity's value: the entity a tag or an id, each part its name or initials",
        };
        Argument<string[]> value = new("value")
        {
            Description = "The entity's value in JSON, or one per field, as 1 2 3 for a position",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command set = new(
            "set",
            "Hands Key=value to the gem the key names; or sets an entity's value, as entity.component.member and on to any depth, then the value"
        )
        {
            setting,
            value,
        };
        set.SetAction(parsed => Set(parsed.GetRequiredValue(setting), parsed.GetValue(value) ?? []));

        return set;

        // A SetCliCommand for one Key=value word; an entity's value for a path and the values after it.
        void Set(string what, string[] values)
        {
            if (values.Length == 0 && what.Contains('=', StringComparison.Ordinal))
                SetSetting(what);
            else
                SetEntityValue(what, values);
        }

        // Publishes Key=value as a SetCliCommand, for the gem its key names.
        void SetSetting(string keyValue)
        {
            int equals = keyValue.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                Log.Error($"set takes Key=value, not '{keyValue}'.");

                return;
            }

            if (!CommandLookups.IsProjectOpen(app, $"set {keyValue} in"))
                return;

            events.Publish(new SetCliCommand(keyValue[..equals].Trim(), keyValue[(equals + 1)..].Trim()));
        }

        // Sets the value at the path on each entity it names that has the component, logging how many it set or why
        // none.
        void SetEntityValue(string path, string[] values)
        {
            if (!CommandLookups.IsProjectOpen(app, $"set {path} in"))
                return;

            Result<int> written = WriteEntityValue(path, values);
            if (written.Ok)
                Log.Info(
                    $"Set {path} to {string.Join(' ', values)} on {written.Value} {(written.Value == 1 ? "entity" : "entities")}."
                );
            else
                Log.Error($"{path} was not set: {written.Error}");
        }

        // Writes the values at the path on each entity it names that has the component: how many it wrote, or why none.
        Result<int> WriteEntityValue(string path, ReadOnlySpan<string> values)
        {
            Result<ImmutableArray<(Entity Entity, IComponent Component)>> found = EntityPaths.FindComponents(
                world,
                path
            );
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

                world.Set(entity, [component]);
            }

            return Result.Success(found.Value.Length);
        }
    }

    /// <summary>
    /// Writes <paramref name="values"/> at <paramref name="members"/> under <paramref name="owner"/>, a box, which it
    /// changes: each value type on the way is a copy, written into the copy above it once changed.
    /// </summary>
    private static Result WriteMembers(object owner, ReadOnlySpan<string> members, ReadOnlySpan<string> values)
    {
        if (EntityPaths.FindMember(owner.GetType(), members[0]) is not { } member)
            return Result.Failure($"{owner.GetType().Name} has no {members[0]}.");

        if (member is PropertyInfo { CanWrite: false })
            return Result.Failure($"{owner.GetType().Name}.{member.Name} follows from other values; set those.");

        if (members.Length == 1)
        {
            Result<object> parsed = ParseValue(EntityPaths.GetValueType(member), values);
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            SetValue(member, owner, parsed.Value);

            return Result.Success();
        }

        if (EntityPaths.GetValue(member, owner) is not { } child)
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
            Result<object> part = ParseOne(EntityPaths.GetValueType(fields[i]), values[i]);
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

    private static void SetValue(MemberInfo member, object owner, object? value)
    {
        if (member is FieldInfo field)
            field.SetValue(owner, value);
        else
            ((PropertyInfo)member).SetValue(owner, value);
    }
}

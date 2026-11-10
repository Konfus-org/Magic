using System.Collections.Immutable;
using System.CommandLine;
using System.Linq;
using System.Reflection;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>set</c>: a gem's setting, by its key, to a value as <see cref="ValueText"/> reads it; <c>Key=value</c> handed to the
/// gem the key names; or an entity's value, named by a path as <see cref="EntityPaths"/> reads it, to a value as
/// <see cref="ValueText"/> reads it (<c>1 2 3</c> for a position).
/// </summary>
internal static class SetCommand
{
    public static Command Create(App app, Settings settings, EventCoordinator events, World world)
    {
        Argument<string> setting = new("setting")
        {
            Description =
                "A setting's key, as Rendering.Resolution; Key=value; or the path to an entity's value: the entity a tag or an id, each part its name or initials",
        };
        Argument<string[]> value = new("value")
        {
            Description = "The value in JSON, or one per field, as 1 2 3 for a position",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command set = new(
            "set",
            "Sets a setting, as Rendering.Resolution 2560 1440; hands Key=value to the gem the key names; or sets an entity's value, as entity.component.member and on to any depth, then the value"
        )
        {
            setting,
            value,
        };
        set.SetAction(parsed => Set(parsed.GetRequiredValue(setting), parsed.GetValue(value) ?? []));

        return set;

        // A setting for its key; a SetCliCommand for one Key=value word; an entity's value for a path and the values
        // after it.
        void Set(string what, string[] values)
        {
            if (settings.All.Any(row => row.Key == what))
                SetSetting(what, string.Join(' ', values));
            else if (values.Length == 0 && what.Contains('=', StringComparison.Ordinal))
                PublishKeyValue(what);
            else
                SetEntityValue(what, values);
        }

        void SetSetting(string key, string text)
        {
            Result set = settings.Set(key, text);
            if (set.Ok)
                Log.Info($"Set {key} to {text}.");
            else
                Log.Error($"{key} was not set: {set.Error}");
        }

        // Publishes Key=value as a SetCliCommand, for the gem its key names.
        void PublishKeyValue(string keyValue)
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
            Result<object> parsed = ValueText.Parse(EntityPaths.GetValueType(member), values);
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            ValueText.SetMember(member, owner, parsed.Value);

            return Result.Success();
        }

        if (EntityPaths.GetValue(member, owner) is not { } child)
            return Result.Failure($"{owner.GetType().Name}.{member.Name} is null.");

        Result written = WriteMembers(child, members[1..], values);
        if (!written.Ok)
            return written;

        ValueText.SetMember(member, owner, child);

        return Result.Success();
    }
}

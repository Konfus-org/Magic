using System.CommandLine;
using System.Linq;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>set</c>: sets a setting of a gem, by its key, or a value of an entity, named by a path, to a value.
/// </summary>
/// <remarks>
/// <see cref="ValueText"/> reads the value (<c>1 2 3</c> for a position), and <see cref="EntityPaths"/> reads the path.
/// </remarks>
internal static class SetCommand
{
    public static Command Create(App app, Settings settings, World world)
    {
        Argument<string> setting = new("setting")
        {
            Description =
                "A setting's key, as Rendering.Resolution; or the path to an entity's value: the entity a tag or an id, each part its name or initials",
        };
        Argument<string[]> value = new("value")
        {
            Description = "The value in JSON, or one per field, as 1 2 3 for a position",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command set = new(
            "set",
            "Sets a setting, as Rendering.Resolution 2560 1440; or sets an entity's value, as entity.component.member and on to any depth, then the value"
        )
        {
            setting,
            value,
        };
        set.SetAction(parsed => Set(parsed.GetRequiredValue(setting), parsed.GetValue(value) ?? []));

        return set;

        void Set(string what, string[] values)
        {
            // A setting for its key: the words read as the type the setting is declared as.
            if (settings.Options.FirstOrDefault(row => row.Key == what) is { Key: not null } option)
            {
                Result<object> parsed = ValueText.Parse(option.Type, values);
                Result set = parsed.Ok ? settings.Set(option.Key, parsed.Value) : Result.Failure(parsed.Error);
                if (set.Ok)
                    Log.Info($"Set {option.Key} to {string.Join(' ', values)}.");
                else
                    Log.Error($"{option.Key} was not set: {set.Error}");

                return;
            }

            // Else an entity's value, on each entity the path names that has the component.
            if (!CommandLookups.IsProjectOpen(app, $"set {what} in"))
                return;

            Result<int> written = EntityPaths.Write(world, what, values);
            if (written.Ok)
                Log.Info(
                    $"Set {what} to {string.Join(' ', values)} on {written.Value} {(written.Value == 1 ? "entity" : "entities")}."
                );
            else
                Log.Error($"{what} was not set: {written.Error}");
        }
    }
}

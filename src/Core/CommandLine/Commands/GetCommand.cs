using System.Collections.Immutable;
using System.CommandLine;
using System.Linq;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>get</c>: prints every gem's settings, a category's or one key's; or an entity's value, named by a path as
/// <see cref="EntityPaths"/> reads it, on each entity the path names.
/// </summary>
internal static class GetCommand
{
    public static Command Create(App app, Settings settings, World world)
    {
        Argument<string?> path = new("path")
        {
            Description =
                "A setting's category or key, as Rendering; or the entity a tag or an id, then the component and its members",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Command get = new(
            "get",
            "Prints the settings, a category's or one; or an entity's value, named as entity.component.member and on to any depth"
        )
        {
            path,
        };
        get.SetAction(parsed => Get(parsed.GetValue(path)));

        return get;

        void Get(string? what)
        {
            // Every setting with nothing named, or the settings of the category or key named.
            Setting[] rows =
            [
                .. settings.Options.Where(row => what is null || row.Key == what || row.Category == what),
            ];
            foreach (Setting row in rows)
            {
                string restarts = row.RequiresRestart ? " Changing it restarts the run." : string.Empty;
                string value = row.Value is { } set ? ValueText.Format(set) : "null";
                Terminal.Write($"{row.Key} {value}: {row.Summary}{restarts}");
            }

            if (rows.Length > 0)
                return;

            if (what is null)
            {
                Log.Info("No gem has settings.");

                return;
            }

            // Else the value at the path on each entity it names, or why there is none.
            if (!CommandLookups.IsProjectOpen(app, $"get {what} in"))
                return;

            Result<ImmutableArray<string>> read = EntityPaths.Read(world, what);
            if (!read.Ok)
            {
                Log.Error($"{what} was not read: {read.Error}");

                return;
            }

            foreach (string line in read.Value)
            {
                Terminal.Write($"{what} of {line}");
            }
        }
    }
}

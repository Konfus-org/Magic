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

        // Every setting with nothing named; the settings of the category or key named; or else the entity's value.
        void Get(string? what)
        {
            Setting[] rows =
            [
                .. settings.Options.Where(row => what is null || row.Key == what || row.Category == what),
            ];
            if (what is not null && rows.Length == 0)
                PrintEntityValue(what);
            else if (rows.Length == 0)
                Log.Info("No gem has settings.");
            else
                PrintSettings(rows);
        }

        static void PrintSettings(Setting[] rows)
        {
            foreach (Setting row in rows)
            {
                string restarts = row.RequiresRestart ? " Changing it restarts the run." : string.Empty;
                string value = row.Value is { } set ? ValueText.Format(set) : "null";
                Terminal.Write($"{row.Key} {value}: {row.Summary}{restarts}");
            }
        }

        // Prints the value at the path on each entity it names, or why there is none.
        void PrintEntityValue(string entityPath)
        {
            if (!CommandLookups.IsProjectOpen(app, $"get {entityPath} in"))
                return;

            Result<ImmutableArray<string>> read = EntityPaths.Read(world, entityPath);
            if (!read.Ok)
            {
                Log.Error($"{entityPath} was not read: {read.Error}");

                return;
            }

            foreach (string line in read.Value)
            {
                Terminal.Write($"{entityPath} of {line}");
            }
        }
    }
}

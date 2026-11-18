using System.CommandLine;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query</c>: prints what the run knows about a subject, or the value at a path that <c>set</c> takes, as one JSON
/// object.
/// </summary>
/// <remarks>
/// A subject is a command under it, as <c>query domain</c>. A first word that is no subject is a path: the key or the
/// category of a setting, a property of a part that has a <see cref="CliPathAttribute"/>, or a value of an entity. A
/// tool reads the JSON instead of working the values out again, and <c>--out</c> writes it to a file.
/// </remarks>
internal static class QueryCommand
{
    /// <summary>
    /// The <c>--out</c> option of <c>query</c>, which each subject under it reads.
    /// </summary>
    public static Option<string> CreateOutput() =>
        new("--out")
        {
            Description = "Write the JSON to this file; without it, the JSON goes to standard output",
            Recursive = true,
        };

    /// <summary>
    /// A subject of <c>query</c> that a gem adds: it prints what <paramref name="read"/> gives, or writes it to the
    /// file that <paramref name="output"/> names.
    /// </summary>
    public static Command CreateSubject(
        IFileSystem files,
        Option<string> output,
        string name,
        string description,
        Func<JsonObject> read
    )
    {
        Command subject = new(name, description);
        subject.SetAction(parsed =>
            CommandLookups.Print(files, parsed.GetValue(output), read(), $"The answer of query {name} is in")
        );

        return subject;
    }

    public static Command Create(
        App app,
        Settings settings,
        IFileSystem files,
        AssetManager assets,
        World world,
        CommandQueue queue,
        ConsoleCommands commands,
        Option<string> output
    )
    {
        Argument<string?> path = new("path")
        {
            Description =
                "A path that set takes: a setting's key or category, as Rendering; or a property of a part of a gem, as Window.Title; or the entity a tag or an id, then the component and its members",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Command query = new(
            "query",
            "Prints what the run knows about a subject, or the value at a path that set takes, as JSON"
        )
        {
            path,
            output,
            DomainQuery.Create(app, files, assets, world, queue, output),
            ComponentsQuery.Create(files, output),
            AssetQuery.Create(app, files, assets, output),
            SettingsQuery.Create(settings, files, output),
            StatsQuery.Create(files, queue, output),
            CreateSubject(
                files,
                output,
                "log",
                "Prints the path of the log file of this process, and how many warnings and errors were logged",
                () =>
                    new JsonObject
                    {
                        ["path"] = Log.FilePath,
                        ["warnings"] = Log.WarningCount,
                        ["errors"] = Log.ErrorCount,
                    }
            ),
        };
        query.Subcommands.Add(
            CreateSubject(
                files,
                output,
                "world",
                "Prints the world's domains and how many entities it holds",
                () => WorldQuery.Describe(assets, world)
            )
        );
        query.SetAction(parsed => Read(parsed.GetValue(path), parsed.GetValue(output)));

        return query;

        void Read(string? what, string? file)
        {
            if (what is null)
            {
                Log.Error("query takes a subject, as query domain, or a path that set takes, as query Rendering.");

                return;
            }

            // A setting's key or category first, as set finds a setting before an entity.
            JsonObject rows = SettingsQuery.Describe(settings, what);
            if (rows["settings"] is JsonArray { Count: > 0 })
            {
                CommandLookups.Print(files, file, rows, "The settings are in");

                return;
            }

            // Else a property of the part of a gem that has the path.
            if (commands.FindService(what) is { } found)
            {
                Result<JsonNode?> value = ServicePaths.Read(found.Service, found.Type, what);
                if (value.Ok)
                    CommandLookups.Print(
                        files,
                        file,
                        new JsonObject { ["path"] = what, ["value"] = value.Value },
                        "The value is in"
                    );
                else
                    Log.Error($"{what} was not read: {value.Error}");

                return;
            }

            // Else the value at the path on each entity it names, or why there is none.
            if (!CommandLookups.IsProjectOpen(app, $"query {what} in"))
                return;

            Result<JsonArray> read = EntityPaths.Read(world, what);
            if (!read.Ok)
            {
                Log.Error($"{what} was not read: {read.Error}");

                return;
            }

            JsonObject values = new() { ["path"] = what, ["values"] = read.Value };
            CommandLookups.Print(files, file, values, "The values are in");
        }
    }
}

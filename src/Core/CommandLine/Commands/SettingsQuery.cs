using System.CommandLine;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Configuration;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query settings</c>: prints the settings of each gem, of a category or of one key.
/// </summary>
internal static class SettingsQuery
{
    public static Command Create(Settings settings, IFileSystem files, Option<string> output)
    {
        Argument<string?> named = new("category-or-key")
        {
            Description = "A setting's category, as Rendering, or its key, as Rendering.Resolution; all when left out",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Command query = new("settings", "Prints the settings, a category's or one, each with its value and summary")
        {
            named,
        };
        query.SetAction(parsed =>
            CommandLookups.Print(
                files,
                parsed.GetValue(output),
                Describe(settings, parsed.GetValue(named)),
                "The settings are in"
            )
        );

        return query;
    }

    /// <summary>
    /// Each setting with nothing named, or the settings of the category or key that <paramref name="named"/> is.
    /// </summary>
    /// <remarks>
    /// A setting holds its key, its value as a file holds it, its summary, and whether a change restarts the run.
    /// </remarks>
    public static JsonObject Describe(Settings settings, string? named)
    {
        JsonArray rows = [];
        foreach (
            Setting row in settings.Options.Where(row => named is null || row.Key == named || row.Category == named)
        )
        {
            Result<JsonElement> value = Json.Write(row.Value, row.Type);
            rows.Add(
                new JsonObject
                {
                    ["key"] = row.Key,
                    ["value"] = value.Ok ? JsonNode.Parse(value.Value.GetRawText()) : null,
                    ["summary"] = row.Summary,
                    ["requiresRestart"] = row.RequiresRestart,
                }
            );
        }

        return new() { ["settings"] = rows };
    }
}

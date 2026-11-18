using System.CommandLine;
using System.Linq;
using System.Text.Json.Nodes;
using Magic.Debugging;
using Magic.Files;
using Magic.Threading;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query stats [category]</c>: prints what the meters measure over the next second.
/// </summary>
/// <remarks>
/// With a category, such as gpu, it prints only each instrument whose name has the category in it. With
/// <c>--every</c>, it logs the values each so many seconds instead, and 0 stops that log.
/// </remarks>
internal static class StatsQuery
{
    /// <summary>
    /// How long one print measures for, so rates have something to be rates of.
    /// </summary>
    private static readonly TimeSpan _sampleTime = TimeSpan.FromSeconds(1);

    public static Command Create(IFileSystem files, CommandQueue queue, Option<string> output)
    {
        Argument<string> category = new("category")
        {
            Description = "A part of the instruments' names, such as gpu; all when left out",
            DefaultValueFactory = _ => MetricsLog.All,
        };
        Option<double?> every = new("--every")
        {
            Description = "Log it every so many seconds, in place of one print; 0 stops the log",
        };
        Command stats = new("stats", "Prints what the meters measure over a second, or a category of it")
        {
            category,
            every,
        };
        stats.SetAction(parsed =>
            Start(parsed.GetRequiredValue(category), parsed.GetValue(every), parsed.GetValue(output))
        );

        return stats;

        void Start(string measured, double? everySec, string? file)
        {
            if (everySec < 0)
            {
                Log.Error($"--every takes a number of seconds, 0 to stop, not {everySec}.");

                return;
            }

            if (everySec is { } seconds)
            {
                MetricsLog.Start(measured, seconds);

                return;
            }

            // The lines after it wait for the second that it measures, so an exit does not leave before it prints.
            Task printing = PrintAsync(measured, file);
            printing.FireAndForget();
            queue.Hold(() => !printing.IsCompleted);
        }

        async Task PrintAsync(string measured, string? file)
        {
            string stats = await MetricsLog
                .SampleAsync(measured, _sampleTime, CancellationToken.None)
                .ConfigureAwait(false);

            // The first line says the category and the time; each line after it is one instrument, as the log has it.
            JsonObject described = new()
            {
                ["category"] = measured,
                ["seconds"] = _sampleTime.TotalSeconds,
                ["stats"] = new JsonArray([.. stats.Split('\n').Skip(1).Select(line => JsonValue.Create(line))]),
            };
            CommandLookups.Print(files, file, described, "The stats are in");
        }
    }
}

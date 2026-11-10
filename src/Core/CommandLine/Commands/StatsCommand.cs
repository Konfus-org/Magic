using System.CommandLine;
using System.Globalization;
using Magic.Debugging;
using Magic.Threading;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>stats [category] [seconds]</c>: prints what the meters measure over the next second, all of it or each
/// instrument with the category as a part of its name, such as gpu; with seconds, logs it every so many seconds
/// instead, 0 to stop.
/// </summary>
internal static class StatsCommand
{
    /// <summary>
    /// How long one print measures for, so rates have something to be rates of.
    /// </summary>
    private static readonly TimeSpan _sampleTime = TimeSpan.FromSeconds(1);

    public static Command Create()
    {
        Argument<string[]> words = new("category-and-seconds")
        {
            Description =
                "A part of the instruments' names, such as gpu, or all when left out; then how many seconds apart to log it, 0 to stop, or none to print it once",
            Arity = new ArgumentArity(0, 2),
        };
        Command stats = new(
            "stats",
            "Prints what the meters measure over a second, or a category of it; with seconds, logs it every so many seconds"
        )
        {
            words,
        };
        stats.SetAction(parsed => Start(parsed.GetValue(words) ?? []));

        return stats;

        // The last word is the seconds when it is a number; the word before it, or the only word, is the category.
        static void Start(string[] given)
        {
            double everySec = 0;
            bool hasSeconds =
                given.Length > 0
                && double.TryParse(given[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out everySec);
            if ((given.Length == 2 && !hasSeconds) || everySec < 0)
            {
                Log.Error($"stats takes a number of seconds, 0 to stop, not '{given[^1]}'.");

                return;
            }

            string category = given.Length == (hasSeconds ? 2 : 1) ? given[0] : MetricsLog.All;
            if (hasSeconds)
                MetricsLog.Start(category, everySec);
            else
                PrintAsync(category).FireAndForget();
        }

        static async Task PrintAsync(string category)
        {
            string stats = await MetricsLog
                .SampleAsync(category, _sampleTime, CancellationToken.None)
                .ConfigureAwait(false);
            Terminal.Write(stats);
        }
    }
}

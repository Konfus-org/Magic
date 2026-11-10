using System.CommandLine;
using System.Globalization;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>stats [category] &lt;seconds&gt;</c>: logs what the meters measure every so many seconds, all of it or each
/// instrument with the category as a part of its name, such as gpu; 0 stops it.
/// </summary>
internal static class StatsCommand
{
    public static Command Create()
    {
        Argument<string[]> words = new("category-and-seconds")
        {
            Description =
                "A part of the instruments' names, such as gpu, or all when left out, then how many seconds apart, 0 to stop",
            Arity = new ArgumentArity(1, 2),
        };
        Command stats = new("stats", "Logs what the meters measure, or a category of it, every so many seconds")
        {
            words,
        };
        stats.SetAction(parsed => Start(parsed.GetRequiredValue(words)));

        return stats;

        static void Start(string[] given)
        {
            string seconds = given[^1];
            if (
                !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double everySec)
                || everySec < 0
            )
            {
                Log.Error($"stats takes a number of seconds, 0 to stop, not '{seconds}'.");

                return;
            }

            MetricsLog.Start(given.Length == 2 ? given[0] : MetricsLog.All, everySec);
        }
    }
}

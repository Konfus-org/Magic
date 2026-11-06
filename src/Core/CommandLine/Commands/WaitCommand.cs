using System.CommandLine;
using System.Globalization;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>wait</c>: holds the lines after it for a number of frames, or of seconds ending in <c>s</c>; for <c>loaded</c>,
/// until the world has content and is not loading; for <c>drawn</c>, until the first frame is drawn.
/// </summary>
internal static class WaitCommand
{
    public static Command Create(CommandQueue queue)
    {
        Argument<string> until = new("until")
        {
            Description = "How many frames pass first, or seconds as 10s, or loaded, or drawn",
        };
        Command wait = new(
            "wait",
            "Holds the lines after it for a number of frames, or of seconds ending in s, or until the world has loaded or the first frame is drawn"
        )
        {
            until,
        };
        wait.SetAction(parsed => Wait(parsed.GetRequiredValue(until)));

        return wait;

        void Wait(string untilWhat)
        {
            if (untilWhat == "loaded")
            {
                queue.WaitForWorld();

                return;
            }

            if (untilWhat == "drawn")
            {
                queue.WaitForDrawn();

                return;
            }

            bool isSeconds = untilWhat.EndsWith('s');
            string amount = isSeconds ? untilWhat[..^1] : untilWhat;
            if (
                !double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out double count)
                || count < 0
            )
            {
                Log.Error(
                    $"wait takes a number of frames, a number of seconds ending in s, loaded or drawn, not '{untilWhat}'."
                );

                return;
            }

            if (isSeconds)
                queue.WaitSeconds(count);
            else
                queue.WaitFrames((int)count);
        }
    }
}

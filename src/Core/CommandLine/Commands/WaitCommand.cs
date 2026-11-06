using System.CommandLine;
using System.Globalization;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>wait</c>: holds the lines after it for a number of frames, or of seconds ending in <c>s</c>; for <c>loaded</c>,
/// until the world has content and is not loading; for <c>drawn</c>, until the first frame is drawn.
/// </summary>
internal sealed class WaitCommand : Command
{
    private readonly CommandQueue _queue;

    public WaitCommand(CommandQueue queue)
        : base(
            "wait",
            "Holds the lines after it for a number of frames, or of seconds ending in s, or until the world has loaded or the first frame is drawn"
        )
    {
        _queue = queue;
        Argument<string> until = new("until")
        {
            Description = "How many frames pass first, or seconds as 10s, or loaded, or drawn",
        };
        Arguments.Add(until);
        Action = new CliCommand(parsed => Wait(parsed.GetRequiredValue(until)));
    }

    private void Wait(string until)
    {
        if (until == "loaded")
        {
            _queue.WaitForWorld();

            return;
        }

        if (until == "drawn")
        {
            _queue.WaitForDrawn();

            return;
        }

        bool isSeconds = until.EndsWith('s');
        string amount = isSeconds ? until[..^1] : until;
        if (!double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out double count) || count < 0)
        {
            Log.Error(
                $"wait takes a number of frames, a number of seconds ending in s, loaded or drawn, not '{until}'."
            );

            return;
        }

        if (isSeconds)
            _queue.WaitSeconds(count);
        else
            _queue.WaitFrames((int)count);
    }
}

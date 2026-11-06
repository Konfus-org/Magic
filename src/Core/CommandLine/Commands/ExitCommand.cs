using System.CommandLine;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>exit</c>: ends the run and exits; with <c>--now</c>, exits the process at once, without ending the run.
/// </summary>
internal sealed class ExitCommand : Command
{
    private readonly App _app;

    public ExitCommand(App app)
        : base("exit", "Ends the run and exits")
    {
        _app = app;
        Option<bool> now = new("--now") { Description = "Exit at once, without ending the run" };
        Options.Add(now);
        Action = new CliCommand(parsed => Exit(parsed.GetValue(now)));
    }

    private void Exit(bool isNow)
    {
        if (!isNow)
        {
            Log.Info("Exiting.");
            _app.Exit();
            return;
        }

        Log.Info("Exiting immediately with code 0.");
        Log.Flush();
        Environment.Exit(0);
    }
}

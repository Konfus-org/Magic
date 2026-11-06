using System.CommandLine;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>exit</c>: ends the run and exits; with <c>--now</c>, exits the process at once, without ending the run.
/// </summary>
internal static class ExitCommand
{
    public static Command Create(App app)
    {
        Option<bool> now = new("--now") { Description = "Exit at once, without ending the run" };
        Command exit = new("exit", "Ends the run and exits") { now };
        exit.SetAction(parsed => Exit(parsed.GetValue(now)));

        return exit;

        void Exit(bool isNow)
        {
            if (!isNow)
            {
                Log.Info("Exiting.");
                app.Exit();
                return;
            }

            Log.Info("Exiting immediately with code 0.");
            Log.Flush();
            Environment.Exit(0);
        }
    }
}

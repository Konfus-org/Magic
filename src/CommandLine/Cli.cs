using System.CommandLine;
using System.Linq;
using Magic.Debugging;
using Magic.Host;
using Magic.State;

namespace Magic.CommandLine;

/// <summary>
/// Magic's commands, run against the <see cref="App"/>: the command line Magic was launched with is the first of
/// them, and each line typed into the terminal runs the same commands.
/// </summary>
public static class Cli
{
    /// <summary>
    /// Runs the commands Magic was launched with, or says why they didn't parse.
    /// </summary>
    public static Result Run(App app, string[] commandLine) => Invoke(CreateCommands(app).Parse(commandLine));

    /// <summary>
    /// Runs one line of commands, which may leave out the leading --, or says why the line didn't parse.
    /// </summary>
    public static Result Run(App app, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Result.Success();

        string command = line.StartsWith('-') ? line : "--" + line;

        return Invoke(CreateCommands(app).Parse(command));
    }

    /// <summary>
    /// Runs the lines typed into the terminal since the last pump. Stops after a line that ends the run, so the caller
    /// acts on it before the lines after it run.
    /// </summary>
    public static void Pump(App app)
    {
        while (app.IsRunning && Terminal.TryReadLine(out string? line))
        {
            Result ran = Run(app, line);
            if (!ran.Ok)
                Log.Error($"Failed to parse command '{line}': {ran.Error}");
        }
    }

    /// <summary>
    /// The commands, as options whose actions change <paramref name="app"/>; only --verbosity and --exit-now act at
    /// once.
    /// </summary>
    private static RootCommand CreateCommands(App app)
    {
        RootCommand commands = new("Runs a Magic project.")
        {
            Options =
            {
                new Option<string>("--project")
                {
                    Description = "Opens a project at the given path",
                    Action = new Command(cmds => app.ProjectPath = cmds.GetRequiredValue<string>("--project")),
                },
                new Option<bool>("--headless")
                {
                    Description = "Run without a GUI (headless mode)",
                    Action = new Command(cmds => app.IsHeadless = cmds.GetValue<bool>("--headless")),
                },
                new Option<Log.VerbosityLevel>("--verbosity")
                {
                    Description = "How much the log prints",
                    Action = new Command(cmds => Log.Verbosity = cmds.GetValue<Log.VerbosityLevel>("--verbosity")),
                },
                new Option<bool>("--exit")
                {
                    Description = "Tell app to close",
                    Action = new Command(_ =>
                    {
                        Log.Info("Exiting.");
                        app.Exit();
                    }),
                },
                new Option<bool>("--exit-now")
                {
                    Description = "Tell app to close immediately",
                    Action = new Command(_ =>
                    {
                        Log.Info("Exiting immediately with code 0.");
                        Log.Flush();
                        Environment.Exit(0);
                    }),
                },
                new Option<bool>("--fail-on-error")
                {
                    Description = "Exit with code 2 as soon as an error is logged",
                    Action = new Command(cmds => app.FailOnError = cmds.GetValue<bool>("--fail-on-error")),
                },
            },
        };

        // An option's action runs only before its command's own, so the root has one, which does nothing.
        commands.SetAction(_ => { });

        return commands;
    }

    private static Result Invoke(ParseResult parsed)
    {
        if (parsed.Errors.Count > 0)
            return Result.Failure(string.Join("; ", parsed.Errors.Select(error => error.Message)));

        parsed.Invoke();

        return Result.Success();
    }
}

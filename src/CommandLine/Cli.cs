using System.CommandLine;
using System.Linq;
using Magic.Debugging;
using Magic.Host;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// Magic's commands, run against the <see cref="App"/>: the command line Magic was launched with is the first of
/// them, and each line typed into the terminal runs the same commands.
/// </summary>
public static class Cli
{
    /// <summary>
    /// Runs the commands Magic was launched with, or says why they didn't run. Only open runs a project; any other
    /// command does its job and leaves the app exiting.
    /// </summary>
    public static Result Run(App app, string[] commandLine)
    {
        RootCommand commands = CreateCommands(app);
        ParseResult parsed = commands.Parse(commandLine);
        if (parsed.Errors.Count == 0 && parsed.Action == commands.Action)
            return Result.Failure(
                "it has no command; open a project with 'Magic open <project>', or see 'Magic --help'."
            );

        Result ran = Invoke(parsed);
        if (app.ProjectPath.Length == 0)
            app.Exit();

        return ran;
    }

    /// <summary>
    /// Runs one typed line, or says why it didn't run. A line that doesn't start with a command is options, which may
    /// leave out the leading --, or the - of a one-letter option.
    /// </summary>
    public static Result Run(App app, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Result.Success();

        RootCommand commands = CreateCommands(app);
        string firstWord = line.Split(' ', 2)[0];
        if (line.StartsWith('-') || commands.Subcommands.Any(command => command.Name == firstWord))
            return Invoke(commands.Parse(line));

        string dashes = line.Length == 1 ? "-" : "--";

        return Invoke(commands.Parse(dashes + line));
    }

    /// <summary>
    /// Runs the lines typed into the terminal since the last pump. Stops after a line that ends the run, so the caller
    /// acts on it before the lines after it run.
    /// </summary>
    public static void Pump(App app)
    {
        while (app.IsRunning && Terminal.Pump(out string? line))
        {
            Result ran = Run(app, line);
            if (!ran.Ok)
                Log.Error($"Couldn't run '{line}': {ran.Error}");
        }
    }

    /// <summary>
    /// The commands: verbs that act on <paramref name="app"/> or do a job of their own, and options that set how it
    /// runs, given before or after the verb.
    /// </summary>
    private static RootCommand CreateCommands(App app)
    {
        RootCommand commands = new("Magic CLI.\nAllows interaction with the Magic game engine from the command line.")
        {
            Subcommands =
            {
                new Command("open", "Opens the project at the given path and runs it, ending the run of any other")
                {
                    Arguments = { new Argument<string>("project") { Description = "The .magic file to run" } },
                    Action = new CliCommand(cmds => app.ProjectPath = cmds.GetRequiredValue<string>("project")),
                },
                new Command("new", "Makes a project, gem or module from Magic's dotnet new templates")
                {
                    Arguments =
                    {
                        new Argument<string>("template") { Description = "Which template: X in dotnet new magic-X" },
                        new Argument<string>("name") { Description = "What to call it" },
                    },

                    // Options Magic doesn't know, like -o or --force, go to dotnet new.
                    TreatUnmatchedTokensAsErrors = false,
                    Action = new CliCommand(cmds =>
                    {
                        string name = cmds.GetRequiredValue<string>("name");
                        string template = $"magic-{cmds.GetRequiredValue<string>("template")}";
                        Result created = Processes.Run(
                            "dotnet",
                            ["new", template, "--name", name, .. cmds.UnmatchedTokens]
                        );
                        if (!created.Ok)
                            Log.Error($"Couldn't make {name} from {template}: {created.Error}");
                    }),
                },
                new Command("exit", "Ends the run and exits")
                {
                    Options = { new Option<bool>("--now") { Description = "Exit at once, without ending the run" } },
                    Action = new CliCommand(cmds =>
                    {
                        if (!cmds.GetValue<bool>("--now"))
                        {
                            Log.Info("Exiting.");
                            app.Exit();
                            return;
                        }

                        Log.Info("Exiting immediately with code 0.");
                        Log.Flush();
                        Environment.Exit(0);
                    }),
                },
            },
            Options =
            {
                new Option<bool>("--headless")
                {
                    Description = "Run without a GUI (headless mode)",
                    Recursive = true,
                    Action = new CliCommand(cmds => app.IsHeadless = cmds.GetValue<bool>("--headless")),
                },
                new Option<Log.VerbosityLevel>("--verbosity", "-v")
                {
                    Description = "How much the log prints",
                    Recursive = true,
                    Action = new CliCommand(cmds => Log.Verbosity = cmds.GetValue<Log.VerbosityLevel>("--verbosity")),
                },
                new Option<bool>("--fail-on-error")
                {
                    Description = "Exit with code 2 as soon as an error is logged",
                    Recursive = true,
                    Action = new CliCommand(cmds => app.ShouldExitOnError = cmds.GetValue<bool>("--fail-on-error")),
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

using System.CommandLine;
using System.Linq;
using Magic.Debugging;
using Magic.Files;
using Magic.Runtime;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// Magic's commands, run against the <see cref="App"/>: the command line Magic was launched with is the first of
/// them, and each line typed into the terminal runs the same commands.
/// </summary>
internal sealed class Cli
{
    /// <summary>
    /// The typed lines that ended earlier runs without exiting, such as <c>open Other.magic</c>, in order: each run
    /// starts anew from the command line and then these. Static because each run has its own Cli.
    /// </summary>
    private static readonly List<string> _restarts = [];

    private readonly App _app;
    private readonly IFileSystem _files;
    private readonly RootCommand _commands;

    public Cli(App app, IFileSystem files)
    {
        _app = app;
        _files = files;
        _commands = CreateCommands();
    }

    /// <summary>
    /// Executes the commands Magic was launched with, then the typed lines that ended earlier runs, or says why the
    /// commands didn't run. Only open runs a project; any other command does its job and leaves the app exiting.
    /// </summary>
    public Result Execute(string[] commands)
    {
        ParseResult parsed = _commands.Parse(commands);
        if (parsed.Errors.Count == 0 && parsed.Action == _commands.Action)
            return Result.Failure(
                "it has no command; open a project with 'Magic open <project>', or see 'Magic --help'."
            );

        Result ran = Invoke(parsed);
        foreach (string line in _restarts)
        {
            Execute(line);
        }

        if (_app.Project is null)
            _app.Exit();

        return ran;
    }

    /// <summary>
    /// Executes one typed command, or says why it didn't run. One that doesn't start with a verb is options, which may
    /// leave out the leading --, or the - of a one-letter option.
    /// </summary>
    public Result Execute(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result.Success();

        string firstWord = command.Split(' ', 2)[0];
        if (command.StartsWith('-') || _commands.Subcommands.Any(subcommand => subcommand.Name == firstWord))
            return Invoke(_commands.Parse(command));

        string dashes = command.Length == 1 ? "-" : "--";

        return Invoke(_commands.Parse(dashes + command));
    }

    /// <summary>
    /// Executes the next line typed into the terminal, if one is waiting. One that ends the run without exiting is kept,
    /// for the runs after it.
    /// </summary>
    public void Pump()
    {
        if (!Terminal.Pump(out string? line))
            return;

        Result ran = Execute(line);
        if (!ran.Ok)
            Log.Error($"Couldn't run '{line}': {ran.Error}");

        if (!_app.IsRunning && !_app.IsExiting)
            _restarts.Add(line);
    }

    private static Result Invoke(ParseResult parsed)
    {
        if (parsed.Errors.Count > 0)
            return Result.Failure(string.Join("; ", parsed.Errors.Select(error => error.Message)));

        parsed.Invoke();

        return Result.Success();
    }

    /// <summary>
    /// The commands: verbs that act on the app or do a job of their own, and options that set how it runs, given before
    /// or after the verb.
    /// </summary>
    private RootCommand CreateCommands()
    {
        RootCommand commands = new("Magic CLI.\nAllows interaction with the Magic game engine from the command line.")
        {
            Subcommands =
            {
                new Command("open", "Opens the project at the given path and runs it, ending the run of any other")
                {
                    Arguments = { new Argument<string>("project") { Description = "The .magic file to run" } },
                    Action = new CliCommand(cmds => Open(cmds.GetRequiredValue<string>("project"))),
                },
                new Command("new", "Makes a project or gem from Magic's dotnet new templates")
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
                            _app.Exit();
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
                    Action = new CliCommand(cmds => _app.IsHeadless = cmds.GetValue<bool>("--headless")),
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
                    Action = new CliCommand(cmds => _app.ShouldExitOnError = cmds.GetValue<bool>("--fail-on-error")),
                },
            },
        };

        // An option's action runs only before its command's own, so the root has one, which does nothing.
        commands.SetAction(_ => { });

        return commands;
    }

    /// <summary>
    /// Reads the project at <paramref name="path"/> and has the app run it; one that won't read is logged as an error,
    /// and whatever runs goes on.
    /// </summary>
    private void Open(string path)
    {
        Result<Project> project = Project.Load(path, _files);
        if (!project.Ok)
        {
            Log.Error(project.Error);
            return;
        }

        _app.Project = project.Value;
    }
}

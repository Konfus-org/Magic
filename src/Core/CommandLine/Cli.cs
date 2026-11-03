using System.CommandLine;
using System.Globalization;
using System.Linq;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Images;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

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
    private readonly EventCoordinator _events;
    private readonly RootCommand _commands;
    private int _waitFrames;
    private bool _isWaitingForWorld;
    private bool _isOpeningDomain;

    public Cli(App app, IFileSystem files, EventCoordinator events)
    {
        _app = app;
        _files = files;
        _events = events;
        _commands = CreateCommands();
    }

    /// <summary>
    /// The run's world, which <c>wait loaded</c> watches and <c>open</c> opens a domain in; none before a run.
    /// </summary>
    internal World? World { get; set; }

    /// <summary>
    /// The run's assets, which <c>open</c> finds a domain in; none before a run.
    /// </summary>
    internal AssetDatabase? Assets { get; set; }

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
        if (_waitFrames > 0)
        {
            _waitFrames--;

            return;
        }

        if (_isWaitingForWorld)
        {
            if (_isOpeningDomain || World is not { } world || world.IsLoading || world.Contents.IsEmpty)
                return;

            _isWaitingForWorld = false;
        }

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
    /// Publishes a setting, <c>Key=value</c>, for the gem its key names.
    /// </summary>
    private void Set(string setting)
    {
        int equals = setting.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            Log.Error($"set takes Key=value, not '{setting}'.");

            return;
        }

        _events.Publish(new SettingChanged(setting[..equals].Trim(), setting[(equals + 1)..].Trim()));
    }

    /// <summary>
    /// Holds the lines after this one until <paramref name="until"/> frames have passed, or, for <c>loaded</c>, until
    /// the world has content and is not loading.
    /// </summary>
    private void Wait(string until)
    {
        if (until == "loaded")
        {
            _isWaitingForWorld = true;

            return;
        }

        if (int.TryParse(until, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
            _waitFrames = Math.Max(frames, 0);
        else
            Log.Error($"wait takes a number of frames or loaded, not '{until}'.");
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
                new Command(
                    "open",
                    "Opens the project at the given path and runs it, ending the run of any other; or, for a .domain, opens that domain in place of the world"
                )
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
                new Command("screenshot", "Saves what the next frame draws as a PNG under Screenshots/ beside Magic")
                {
                    Action = new CliCommand(cmds =>
                    {
                        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                        string path = _files.Combine(AppContext.BaseDirectory, "Screenshots", $"{name}.png");
                        _events.Publish(new ScreenshotRequested(path));
                    }),
                },
                new Command("set", "Sets a gem's setting for the run, as Key=value, which the gem the key names reads")
                {
                    Arguments =
                    {
                        new Argument<string>("setting") { Description = "The key and the value, as Key=value" },
                    },
                    Action = new CliCommand(cmds => Set(cmds.GetRequiredValue<string>("setting"))),
                },
                new Command("wait", "Holds the lines after it for a number of frames, or until the world has loaded")
                {
                    Arguments =
                    {
                        new Argument<string>("until") { Description = "How many frames pass first, or loaded" },
                    },
                    Action = new CliCommand(cmds => Wait(cmds.GetRequiredValue<string>("until"))),
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
        if (path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
        {
            OpenDomainAsync(path).FireAndForget();

            return;
        }

        Result<Project> project = Project.Load(path, _files);
        if (!project.Ok)
        {
            Log.Error(project.Error);
            return;
        }

        _app.Project = project.Value;
    }

    /// <summary>
    /// Opens the domain at <paramref name="path"/> in place of the world, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task OpenDomainAsync(string path)
    {
        if (World is not { } world || Assets is not { } assets)
        {
            Log.Error($"No project is running to open {path} in.");

            return;
        }

        Handle<Domain> domain = assets.Find<Domain>(path);
        if (domain == Handle<Domain>.None)
        {
            Log.Error($"There is no domain at {path}.");

            return;
        }

        _isOpeningDomain = true;
        try
        {
            Result<WorldContent> opened = await world
                .OpenAsync(domain, DomainOpenMode.Replace, null, CancellationToken.None)
                .ConfigureAwait(false);
            if (!opened.Ok)
                Log.Error($"{path} did not open: {opened.Error}");
        }
        finally
        {
            _isOpeningDomain = false;
        }
    }
}

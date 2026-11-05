using System.CommandLine;
using System.Globalization;
using System.Linq;
using Magic.CommandLine.Commands;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Files.Types;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine;

/// <summary>
/// Magic's commands, run against the <see cref="App"/>: the command line Magic was launched with is the first of
/// them, and each line typed into the terminal runs the same commands.
/// </summary>
internal sealed class Cli : IDisposable
{
    private readonly App _app;

    private readonly IFileSystem _files;

    private readonly EventCoordinator _events;

    private readonly Assets _assets;

    private readonly World _world;

    private readonly CommandQueue _queue;

    private readonly RootCommand _cliCommands;

    public Cli(App app, IFileSystem files, EventCoordinator events, Assets assets, World world)
    {
        _app = app;
        _files = files;
        _events = events;
        _assets = assets;
        _world = world;
        _queue = new CommandQueue(world, events);
        _cliCommands = CreateCliCommands();
    }

    public void Dispose() => _queue.Dispose();

    /// <summary>
    /// Executes the commands Magic was launched with, or says why they didn't run. Only open runs a project; any other
    /// command does its job and leaves the app exiting.
    /// </summary>
    public Result Execute(string[] commands)
    {
        ParseResult parsed = _cliCommands.Parse(commands);
        if (parsed.Errors.Count == 0 && parsed.Action == _cliCommands.Action)
            return Result.Failure(
                "it has no command; open a project with 'Magic open <project>', or see 'Magic --help'."
            );

        Result ran = Invoke(parsed);
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
        if (command.StartsWith('-') || _cliCommands.Subcommands.Any(subcommand => subcommand.Name == firstWord))
            return Invoke(_cliCommands.Parse(command));

        string dashes = command.Length == 1 ? "-" : "--";

        return Invoke(_cliCommands.Parse(dashes + command));
    }

    /// <summary>
    /// Executes the next line, queued or typed into the terminal, once nothing holds it back.
    /// </summary>
    public void Pump()
    {
        if (!_queue.TryDequeue(out string? line))
            return;

        Result ran = Execute(line);
        if (!ran.Ok)
            Log.Error($"Couldn't run '{line}': {ran.Error}");
    }

    private static Result Invoke(ParseResult parsed)
    {
        if (parsed.Errors.Count > 0)
            return Result.Failure(string.Join("; ", parsed.Errors.Select(error => error.Message)));

        parsed.Invoke();

        return Result.Success();
    }

    /// <summary>
    /// Asks whatever draws the frame to save the next one as a PNG under <c>Screenshots/</c> beside Magic.
    /// </summary>
    private void Screenshot()
    {
        if (!CommandLookups.IsProjectOpen(_app, "screenshot"))
            return;

        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string path = _files.Combine(AppContext.BaseDirectory, "Screenshots", $"{name}.png");
        _events.Publish(new ScreenshotRequested(path));
    }

    /// <summary>
    /// Queues the lines that open every domain, wait for each to load and settle, and exit.
    /// </summary>
    private void Bake()
    {
        if (!CommandLookups.IsProjectOpen(_app, "bake"))
            return;

        int domains = 0;
        foreach (string path in _assets.GetPathsUnder("Domains"))
        {
            if (!path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
                continue;

            domains++;
            _queue.Enqueue($"open {path}");
            _queue.Enqueue("wait loaded");
            _queue.Enqueue("wait 120");
        }

        Log.Info($"Baking {domains} domains.");
        _queue.Enqueue("exit");
    }

    /// <summary>
    /// Holds the lines after this one until <paramref name="until"/> frames have passed, or that many seconds for a
    /// number ending in <c>s</c>; for <c>loaded</c>, until the world has content and is not loading; for <c>drawn</c>, until
    /// the first frame is drawn.
    /// </summary>
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

    /// <summary>
    /// The commands: verbs that act on the app or do a job of their own, and options that set how it runs, given before
    /// or after the verb.
    /// </summary>
    private RootCommand CreateCliCommands()
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
                    Action = new CliCommand(cmds => Screenshot()),
                },
                new Command("save", "Saves the game as Saves/<name>.save beside Magic")
                {
                    Arguments = { new Argument<string>("name") { Description = "The save's name" } },
                    Action = new CliCommand(cmds =>
                        SaveGameAsync(cmds.GetRequiredValue<string>("name")).FireAndForget()
                    ),
                },
                new Command("load", "Puts the game back as Saves/<name>.save beside Magic holds it")
                {
                    Arguments = { new Argument<string>("name") { Description = "The save's name" } },
                    Action = new CliCommand(cmds =>
                        LoadGameAsync(cmds.GetRequiredValue<string>("name")).FireAndForget()
                    ),
                },
                new Command("bake", "Opens every domain under Domains/ in turn, so their caches are made, then exits")
                {
                    Action = new CliCommand(cmds => Bake()),
                },
                new SetCommand(_app, _events, _world),
                new GetCommand(_app, _world),
                new TeleportCommand(_app, _world, _assets, _queue),
                new SpawnCommand(_app, _world, _assets, _queue),
                new Command(
                    "wait",
                    "Holds the lines after it for a number of frames, or of seconds ending in s, or until the world has loaded or the first frame is drawn"
                )
                {
                    Arguments =
                    {
                        new Argument<string>("until")
                        {
                            Description = "How many frames pass first, or seconds as 10s, or loaded, or drawn",
                        },
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
    /// Saves the game as <paramref name="name"/>, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task SaveGameAsync(string name)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"save {name} in"))
            return;

        Result saved = await _queue
            .ChangeWorldAsync(() => _world.SaveAsync(name, CancellationToken.None))
            .ConfigureAwait(false);
        if (saved.Ok)
            Log.Info($"Saved the game as {name}.");
        else
            Log.Error($"The game was not saved as {name}: {saved.Error}");
    }

    /// <summary>
    /// Puts the game back as the save <paramref name="name"/> holds it, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task LoadGameAsync(string name)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"load {name} in"))
            return;

        Result loaded = await _queue
            .ChangeWorldAsync(() => _world.RestoreAsync(name, CancellationToken.None))
            .ConfigureAwait(false);
        if (!loaded.Ok)
            Log.Error($"The save {name} was not loaded: {loaded.Error}");
    }

    /// <summary>
    /// Opens the domain at <paramref name="path"/> in place of the world, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task OpenDomainAsync(string path)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"open {path} in"))
            return;

        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (domain == Handle<Domain>.None)
        {
            Log.Error($"There is no domain at {path}.");

            return;
        }

        Result opened = await _queue
            .ChangeWorldAsync(() => _world.OpenAsync(domain, DomainOpenMode.Replace, null, CancellationToken.None))
            .ConfigureAwait(false);
        if (!opened.Ok)
            Log.Error($"{path} did not open: {opened.Error}");
    }
}

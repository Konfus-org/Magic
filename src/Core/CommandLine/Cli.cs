using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
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
    /// <summary>
    /// How far in front of the player a prefab spawned without a place goes.
    /// </summary>
    private const float SpawnAheadMetres = 3;

    private const string TeleportUsage = "teleport takes x y z, a domain, or a domain then x y z";

    private readonly App _app;

    private readonly IFileSystem _files;

    private readonly EventCoordinator _events;

    private readonly Assets _assets;

    private readonly World _world;

    private readonly RootCommand _cliCommands;

    private int _waitFrames;

    /// <summary>
    /// When a wait in seconds ends, as <see cref="Stopwatch.GetTimestamp"/> counts; 0 when none is on.
    /// </summary>
    private long _waitUntilTimestamp;

    private bool _isWaitingForWorld;

    private readonly Queue<string> _queued = new();

    private bool _isChangingWorld;

    private bool _isWaitingToDraw;

    private bool _hasDrawn;

    private readonly Subscription _drawnWatch;

    public Cli(App app, IFileSystem files, EventCoordinator events, Assets assets, World world)
    {
        _app = app;
        _files = files;
        _events = events;
        _assets = assets;
        _world = world;
        _cliCommands = CreateCliCommands();
        _drawnWatch = events.Watch<FirstFrameDrawn>(_ => _hasDrawn = true);
    }

    public void Dispose() => _drawnWatch.Dispose();

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
    /// Executes the next line typed into the terminal, if one is waiting.
    /// </summary>
    public void Pump()
    {
        if (_waitFrames > 0)
        {
            _waitFrames--;

            return;
        }

        if (_waitUntilTimestamp != 0)
        {
            if (Stopwatch.GetTimestamp() < _waitUntilTimestamp)
                return;

            _waitUntilTimestamp = 0;
        }

        if (_isWaitingForWorld)
        {
            if (_isChangingWorld || _world.IsLoading || _world.Domains.IsEmpty)
                return;

            _isWaitingForWorld = false;
        }

        if (_isWaitingToDraw)
        {
            if (!_hasDrawn)
                return;

            _isWaitingToDraw = false;
        }

        if (!_queued.TryDequeue(out string? line) && !Terminal.Pump(out line))
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
    /// The three numbers of <paramref name="xyz"/> as a position, or why they are not one.
    /// </summary>
    private static Result<Vector3> ParsePosition(ReadOnlySpan<string> xyz)
    {
        if (xyz.Length != 3)
            return Result.Failure($"a position is three numbers, x y z, not {xyz.Length}.");

        Span<float> numbers = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(xyz[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return Result.Failure($"'{xyz[i]}' is not a number.");
        }

        return Result.Success(new Vector3(numbers));
    }

    /// <summary>
    /// Whether a project is open for a command that needs one to <paramref name="what"/>; it logs an error when none is.
    /// </summary>
    private bool IsProjectOpen(string what)
    {
        if (_app.Project is not null)
            return true;

        Log.Error($"No project is running to {what}.");

        return false;
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

        if (!IsProjectOpen($"set {setting} in"))
            return;

        _events.Publish(new SettingChanged(setting[..equals].Trim(), setting[(equals + 1)..].Trim()));
    }

    /// <summary>
    /// Sets the value at <paramref name="path"/> on the entities it names to <paramref name="values"/>
    /// (<see cref="EntityPaths.Write"/>), logging how many it set or why none.
    /// </summary>
    private void SetEntityValue(string path, string[] values)
    {
        if (!IsProjectOpen($"set {path} in"))
            return;

        Result<int> written = EntityPaths.Write(_world, path, values);
        if (written.Ok)
            Log.Info(
                $"Set {path} to {string.Join(' ', values)} on {written.Value} {(written.Value == 1 ? "entity" : "entities")}."
            );
        else
            Log.Error($"{path} was not set: {written.Error}");
    }

    /// <summary>
    /// Logs the value at <paramref name="path"/> on each entity it names (<see cref="EntityPaths.Read"/>), or why there
    /// is none.
    /// </summary>
    private void LogEntityValue(string path)
    {
        if (!IsProjectOpen($"get {path} in"))
            return;

        Result<ImmutableArray<string>> read = EntityPaths.Read(_world, path);
        if (!read.Ok)
        {
            Log.Error($"{path} was not read: {read.Error}");

            return;
        }

        foreach (string line in read.Value)
        {
            Log.Info($"{path} of {line}");
        }
    }

    /// <summary>
    /// Moves the player to x y z, with three words; with a domain first, opens it in place of the world, which puts the
    /// player at its spawn, then moves the player to the x y z after it, if any, once it has loaded.
    /// </summary>
    private void Teleport(string[] where)
    {
        if (!IsProjectOpen("teleport in"))
            return;

        if (where.Length == 3)
        {
            MovePlayer(where);

            return;
        }

        if (where.Length == 2)
        {
            Log.Error($"{TeleportUsage}, not two words.");

            return;
        }

        Handle<Domain> domain = FindAsset<Domain>(where[0]);
        if (_assets.GetPath(domain) is not { } path)
        {
            Log.Error($"There is no domain at or named {where[0]}.");

            return;
        }

        Result<Vector3> position = where.Length == 4 ? ParsePosition(where.AsSpan(1)) : Result.Success(Vector3.Zero);
        if (!position.Ok)
        {
            Log.Error($"{TeleportUsage}: {position.Error}");

            return;
        }

        OpenDomainAsync(path).FireAndForget();
        if (where.Length != 4)
            return;

        _queued.Enqueue("wait loaded");
        _queued.Enqueue($"teleport {string.Join(' ', where[1..])}");
    }

    /// <summary>
    /// Moves every entity tagged player by how far the first is from the x y z of <paramref name="to"/>, so they keep
    /// their places around it.
    /// </summary>
    private void MovePlayer(string[] to)
    {
        Result<Vector3> position = ParsePosition(to);
        if (!position.Ok)
        {
            Log.Error($"{TeleportUsage}: {position.Error}");

            return;
        }

        if (FindPlayer() is not { } player)
        {
            Log.Error("There is no entity tagged player with a transform to teleport.");

            return;
        }

        Vector3 by = position.Value - player.Position;
        foreach (EcsChunk<Tags, Transform> chunk in _world.Query<Tags, Transform>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Has(Tag.Player))
                    chunk.Second[i].Position += by;
            }
        }

        Log.Info($"Teleported the player to {position.Value}.");
    }

    /// <summary>
    /// Puts the prefab at or named <paramref name="name"/> in the world at the x y z of <paramref name="at"/>, or
    /// <see cref="SpawnAheadMetres"/> in front of the player for none, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private void Spawn(string name, string[] at)
    {
        if (!IsProjectOpen($"spawn {name} in"))
            return;

        Handle<Prefab> prefab = FindAsset<Prefab>(name);
        if (prefab == Handle<Prefab>.None)
        {
            Log.Error($"There is no prefab at or named {name}.");

            return;
        }

        Result<Vector3> position = at.Length == 0 ? Result.Success(GetAheadOfPlayer()) : ParsePosition(at);
        if (!position.Ok)
        {
            Log.Error($"spawn {name}: {position.Error}");

            return;
        }

        SpawnAsync(prefab, name, new Transform { Position = position.Value }).FireAndForget();
    }

    private async Task SpawnAsync(Handle<Prefab> prefab, string name, Transform at)
    {
        _isChangingWorld = true;
        try
        {
            Result<ImmutableArray<Entity>> spawned = await _world
                .SpawnAsync(prefab, at, null, CancellationToken.None)
                .ConfigureAwait(false);
            if (spawned.Ok)
            {
                string ids = string.Join(", ", spawned.Value.Select(entity => entity.Id));
                Log.Info($"Spawned {name} at {at.Position} as entities {ids}.");
            }
            else
            {
                Log.Error($"{name} was not spawned: {spawned.Error}");
            }
        }
        finally
        {
            _isChangingWorld = false;
        }
    }

    /// <summary>
    /// <see cref="SpawnAheadMetres"/> in front of the player, the way it faces; the origin when there is no player.
    /// </summary>
    private Vector3 GetAheadOfPlayer() =>
        FindPlayer() is { } player
            ? player.Position + (Vector3.Transform(Vector3.UnitZ, player.Rotation) * SpawnAheadMetres)
            : Vector3.Zero;

    /// <summary>
    /// The transform of the first entity tagged player that has one; null when none has.
    /// </summary>
    private Transform? FindPlayer()
    {
        foreach (EcsChunk<Tags, Transform> chunk in _world.Query<Tags, Transform>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Has(Tag.Player))
                    return chunk.Second[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="pathOrName"/>, or else the first whose file is named it, in any
    /// case and without its extension (<c>Tree</c> for <c>Prefabs/Tree.prefab</c>); none when there is neither.
    /// </summary>
    private Handle<T> FindAsset<T>(string pathOrName)
        where T : IAsset<T>
    {
        Handle<T> atPath = _assets.Find<T>(pathOrName);
        if (atPath != Handle<T>.None)
            return atPath;

        foreach (string path in _assets.Database.Index.ByPath.Keys)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), pathOrName, StringComparison.OrdinalIgnoreCase))
                continue;

            Handle<T> named = _assets.Find<T>(path);
            if (named != Handle<T>.None)
                return named;
        }

        return Handle<T>.None;
    }

    /// <summary>
    /// Asks whatever draws the frame to save the next one as a PNG under <c>Screenshots/</c> beside Magic.
    /// </summary>
    private void Screenshot()
    {
        if (!IsProjectOpen("screenshot"))
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
        if (!IsProjectOpen("bake"))
            return;

        int domains = 0;
        foreach (string path in _assets.GetPathsUnder("Domains"))
        {
            if (!path.EndsWith(".domain", StringComparison.OrdinalIgnoreCase))
                continue;

            domains++;
            _queued.Enqueue($"open {path}");
            _queued.Enqueue("wait loaded");
            _queued.Enqueue("wait 120");
        }

        Log.Info($"Baking {domains} domains.");
        _queued.Enqueue("exit");
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
            _isWaitingForWorld = true;

            return;
        }

        if (until == "drawn")
        {
            _isWaitingToDraw = true;

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
            _waitUntilTimestamp = Stopwatch.GetTimestamp() + (long)(count * Stopwatch.Frequency);
        else
            _waitFrames = (int)count;
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
                new Command(
                    "set",
                    "Sets a gem's setting for the run, as Key=value; or an entity's value, as entity.component.member and on to any depth, then the value"
                )
                {
                    Arguments =
                    {
                        new Argument<string>("setting")
                        {
                            Description =
                                "Key=value, or the path to an entity's value: the entity a tag or an id, each part its name or initials",
                        },
                        new Argument<string[]>("value")
                        {
                            Description = "The entity's value in JSON, or one per field, as 1 2 3 for a position",
                            Arity = ArgumentArity.ZeroOrMore,
                        },
                    },
                    Action = new CliCommand(cmds =>
                    {
                        string setting = cmds.GetRequiredValue<string>("setting");
                        string[] values = cmds.GetValue<string[]>("value") ?? [];
                        if (values.Length == 0 && setting.Contains('=', StringComparison.Ordinal))
                            Set(setting);
                        else
                            SetEntityValue(setting, values);
                    }),
                },
                new Command("get", "Logs an entity's value, named as entity.component.member and on to any depth")
                {
                    Arguments =
                    {
                        new Argument<string>("path")
                        {
                            Description = "The entity a tag or an id, then the component and its members",
                        },
                    },
                    Action = new CliCommand(cmds => LogEntityValue(cmds.GetRequiredValue<string>("path"))),
                },
                new Command(
                    "teleport",
                    "Moves the player to x y z; or opens a domain in place of the world, with the player at its spawn or at x y z"
                )
                {
                    Arguments =
                    {
                        new Argument<string[]>("where")
                        {
                            Description = "x y z; or a domain's path or name, then x y z if wanted",
                            Arity = new ArgumentArity(1, 4),
                        },
                    },
                    Action = new CliCommand(cmds => Teleport(cmds.GetRequiredValue<string[]>("where"))),
                },
                new Command("spawn", "Puts a prefab in the world at x y z, or a few metres in front of the player")
                {
                    Arguments =
                    {
                        new Argument<string>("prefab") { Description = "The prefab's path or name" },
                        new Argument<string[]>("at")
                        {
                            Description = "x y z; none for in front of the player",
                            Arity = ArgumentArity.ZeroOrMore,
                        },
                    },
                    Action = new CliCommand(cmds =>
                        Spawn(cmds.GetRequiredValue<string>("prefab"), cmds.GetValue<string[]>("at") ?? [])
                    ),
                },
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
        if (!IsProjectOpen($"save {name} in"))
            return;

        _isChangingWorld = true;
        try
        {
            Result saved = await _world.SaveAsync(name, CancellationToken.None).ConfigureAwait(false);
            if (saved.Ok)
                Log.Info($"Saved the game as {name}.");
            else
                Log.Error($"The game was not saved as {name}: {saved.Error}");
        }
        finally
        {
            _isChangingWorld = false;
        }
    }

    /// <summary>
    /// Puts the game back as the save <paramref name="name"/> holds it, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task LoadGameAsync(string name)
    {
        if (!IsProjectOpen($"load {name} in"))
            return;

        _isChangingWorld = true;
        try
        {
            Result loaded = await _world.RestoreAsync(name, CancellationToken.None).ConfigureAwait(false);
            if (!loaded.Ok)
                Log.Error($"The save {name} was not loaded: {loaded.Error}");
        }
        finally
        {
            _isChangingWorld = false;
        }
    }

    /// <summary>
    /// Opens the domain at <paramref name="path"/> in place of the world, holding <c>wait loaded</c> meanwhile.
    /// </summary>
    private async Task OpenDomainAsync(string path)
    {
        if (!IsProjectOpen($"open {path} in"))
            return;

        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (domain == Handle<Domain>.None)
        {
            Log.Error($"There is no domain at {path}.");

            return;
        }

        _isChangingWorld = true;
        try
        {
            Result opened = await _world
                .OpenAsync(domain, DomainOpenMode.Replace, null, CancellationToken.None)
                .ConfigureAwait(false);
            if (!opened.Ok)
                Log.Error($"{path} did not open: {opened.Error}");
        }
        finally
        {
            _isChangingWorld = false;
        }
    }
}

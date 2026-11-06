using System.CommandLine;
using System.Linq;
using Magic.CommandLine.Commands;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine;

/// <summary>
/// Magic's commands, run against the <see cref="App"/>: the command line Magic was launched with is the first of
/// them, and each line typed into the terminal runs the same commands.
/// </summary>
internal sealed class Cli : IDisposable
{
    private readonly CommandQueue _queue;

    private readonly RootCommand _cliCommands;

    public Cli(App app, IFileSystem files, EventCoordinator events, Assets assets, World world)
    {
        _queue = new CommandQueue(events);
        _cliCommands = CreateCliCommands(app, files, events, assets, world, _queue);
    }

    public void Dispose() => _queue.Dispose();

    /// <summary>
    /// Executes the commands Magic was launched with, or says why they didn't run. Only open runs a project; any other
    /// command does its job, and Magic exits after it.
    /// </summary>
    public Result Execute(string[] commands)
    {
        ParseResult parsed = _cliCommands.Parse(commands);
        if (parsed.Errors.Count == 0 && parsed.Action == _cliCommands.Action)
            return Result.Failure(
                "it has no command; open a project with 'Magic open <project>', or see 'Magic --help'."
            );

        return Invoke(parsed);
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
    /// The commands, each given only the services it uses: verbs that act on the app or do a job of their own, and
    /// options that set how it runs, given before or after the verb.
    /// </summary>
    private static RootCommand CreateCliCommands(
        App app,
        IFileSystem files,
        EventCoordinator events,
        Assets assets,
        World world,
        CommandQueue queue
    )
    {
        Option<bool> headless = new("--headless")
        {
            Description = "Run without a GUI (headless mode)",
            Recursive = true,
        };
        headless.Action = new OptionAction(parsed => app.IsHeadless = parsed.GetValue(headless));
        Option<Log.VerbosityLevel> verbosity = new("--verbosity", "-v")
        {
            Description = "How much the log prints",
            Recursive = true,
        };
        verbosity.Action = new OptionAction(parsed => Log.Verbosity = parsed.GetValue(verbosity));
        Option<bool> failOnError = new("--fail-on-error")
        {
            Description = "Exit with code 2 as soon as an error is logged",
            Recursive = true,
        };
        failOnError.Action = new OptionAction(parsed => app.ShouldExitOnError = parsed.GetValue(failOnError));
        RootCommand commands = new("Magic CLI.\nAllows interaction with the Magic game engine from the command line.")
        {
            Subcommands =
            {
                OpenCommand.Create(app, files, assets, world, queue),
                NewCommand.Create(),
                ScreenshotCommand.Create(app, files, events),
                SaveCommand.Create(app, world, queue),
                LoadCommand.Create(app, world, queue),
                BakeCommand.Create(app, assets, queue),
                SetCommand.Create(app, events, world),
                GetCommand.Create(app, world),
                TeleportCommand.Create(app, world, assets, queue),
                SpawnCommand.Create(app, world, assets, queue),
                WaitCommand.Create(queue, world),
                ExitCommand.Create(app),
            },
            Options = { headless, verbosity, failOnError },
        };

        // An option's action runs only before its command's own, so the root has one, which does nothing.
        commands.SetAction(_ => { });

        return commands;
    }
}

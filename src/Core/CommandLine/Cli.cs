using System.CommandLine;
using System.IO;
using System.Linq;
using Magic.Assets;
using Magic.CommandLine.Commands;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine;

/// <summary>
/// The commands of Magic, run against the <see cref="App"/>.
/// </summary>
/// <remarks>
/// The command line that Magic started with is the first command, and each line typed into the terminal runs the same
/// commands.
/// </remarks>
internal sealed class Cli
{
    private readonly CommandQueue _queue;

    private readonly RootCommand _cliCommands;

    public Cli(App app, Settings settings, IFileSystem files, EventCoordinator events, AssetManager assets, World world)
    {
        _queue = new CommandQueue(events);

        // The commands, each given only the services it uses: verbs that act on the app or do a job of their own, and
        // options that set how it runs, given before or after the verb.
        _cliCommands = new("Magic CLI.\nAllows interaction with the Magic game engine from the command line.")
        {
            Subcommands =
            {
                OpenCommand.Create(app, files, assets, world, _queue),
                NewCommand.Create(),
                ScreenshotCommand.Create(app, files, events),
                StatsCommand.Create(),
                SaveCommand.Create(app, settings, world, _queue),
                LoadCommand.Create(app, world, _queue),
                BakeCommand.Create(app, assets, _queue),
                SetCommand.Create(app, settings, world),
                GetCommand.Create(app, settings, world),
                TeleportCommand.Create(app, world, assets, _queue),
                SpawnCommand.Create(app, world, assets, _queue),
                WaitCommand.Create(_queue, world),
                NameCommand.Create(app),
                ExitCommand.Create(app),
            },
            Options =
            {
                CreateOption<bool>("--headless", "Run without a GUI (headless mode)", value => app.IsHeadless = value),
                CreateOption<Log.VerbosityLevel>(
                    "--verbosity",
                    "How much the log prints",
                    value => Log.Verbosity = value,
                    "-v"
                ),
                CreateOption<bool>(
                    "--fail-on-error",
                    "Exit with code 2 as soon as an error is logged",
                    value => app.ShouldExitOnError = value
                ),
                CreateOption<string[]>(
                    "--configure",
                    "Set settings as their gems are made, as --configure Window.Mode=windowed Rendering.Resolution=\"1280 720\"",
                    settings.Configure
                ),
            },
        };

        // An option's action runs only before its command's own, so the root has one, which does nothing.
        _cliCommands.SetAction(_ => { });

        // Every option is recursive, and its action applies its value before the command runs. A list option takes each
        // value after it, up to the next option or command.
        static Option<T> CreateOption<T>(string name, string description, Action<T> apply, params string[] aliases)
        {
            Option<T> option = new(name, aliases)
            {
                Description = description,
                Recursive = true,
                AllowMultipleArgumentsPerToken = true,
            };
            option.Action = new OptionAction(parsed => apply(parsed.GetRequiredValue(option)));

            return option;
        }
    }

    /// <summary>
    /// Executes the commands that Magic started with, or returns why they did not run.
    /// </summary>
    /// <remarks>
    /// Only open runs a project. Each other command does its job, and then Magic exits.
    /// </remarks>
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
    /// Executes one typed command, or returns why it did not run.
    /// </summary>
    /// <remarks>
    /// A command that does not start with a verb is options, which can leave out the leading --, or the - of a
    /// one-letter option.
    /// </remarks>
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

    /// <summary>
    /// Runs <paramref name="parsed"/>, or returns why it does not run.
    /// </summary>
    /// <remarks>
    /// What it prints, such as help, goes through the terminal, so the prompt comes back under it. What it throws is
    /// logged.
    /// </remarks>
    private static Result Invoke(ParseResult parsed)
    {
        if (parsed.Errors.Count > 0)
            return Result.Failure(string.Join("; ", parsed.Errors.Select(error => error.Message)));

        using StringWriter output = new();
        using StringWriter errors = new();
        parsed.Invoke(new InvocationConfiguration { Output = output, Error = errors });
        if (output.GetStringBuilder().Length > 0)
            Terminal.Write(output.ToString().TrimEnd());
        if (errors.GetStringBuilder().Length > 0)
            Log.Error(errors.ToString().TrimEnd());

        return Result.Success();
    }
}

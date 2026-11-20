using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// <c>query</c>, which takes the subjects that the gems add.
    /// </summary>
    private readonly Command _query;

    /// <summary>
    /// The <c>--out</c> option of <c>query</c>, which a subject that a gem adds reads as Magic's own do.
    /// </summary>
    private readonly Option<string> _queryOutput = QueryCommand.CreateOutput();

    private readonly IFileSystem _files;

    public Cli(App app, Settings settings, IFileSystem files, EventCoordinator events, AssetManager assets, World world)
    {
        _files = files;
        _queue = new CommandQueue(events);
        Commands = new ConsoleCommands(_queue);
        _query = QueryCommand.Create(app, settings, files, assets, world, _queue, Commands, _queryOutput);

        // The state of the run that Core itself gives a path, each through the few properties that the console shows.
        // They last as long as the console.
        Commands.Register("World", world, typeof(IWorldClock));
        Commands.Register("App", app, typeof(IRunOptions));

        // The commands, each given only the services it uses: verbs that act on the app or do a job of their own, and
        // options that set how it runs, given before or after the verb.
        _cliCommands = new("Magic CLI.\nAllows interaction with the Magic game engine from the command line.")
        {
            Subcommands =
            {
                OpenCommand.Create(app, files, assets, world, _queue),
                NewCommand.Create(app, files, assets, _queue),
                ScreenshotCommand.Create(app, files, events),
                CaptureCommand.Create(app, files, events),
                SaveSettingsCommand.Create(settings),
                BakeCommand.Create(app, assets, _queue),
                ImportCommand.Create(app, files, assets, _queue),
                ExportCommand.Create(app, assets, _queue),
                _query,
                SetCommand.Create(app, settings, assets, world, Commands, _queue),
                SpawnCommand.Create(app, world, assets, _queue),
                WaitCommand.Create(_queue, world),
                ExitCommand.Create(app),
            },
            Options =
            {
                CreateOption<string[]>(
                    "--owner",
                    "Run for an app on this computer, as --owner Wizard 1234, and exit when it stops sending",
                    values => app.Owner = ParseOwner(values)
                ),
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
                CreateOption<string>(
                    "--name",
                    "Name the window from the moment it opens, as --name \"my run\"",
                    value => app.WindowName = value
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

        // An owner is a name and a port, as --owner Wizard 5211.
        static AppOwner? ParseOwner(string[] values)
        {
            if (
                values.Length == 2
                && int.TryParse(values[1], CultureInfo.InvariantCulture, out int port)
                && port is > 0 and <= ushort.MaxValue
            )
                return new AppOwner(values[0], port);

            Log.Error(
                $"--owner {string.Join(' ', values)} was not set: give a name and a port, as --owner Wizard 1234."
            );

            return null;
        }
    }

    /// <summary>
    /// The commands that the gems of the run add, which join the console as each gem registers one.
    /// </summary>
    public ConsoleCommands Commands { get; }

    /// <summary>
    /// Executes the commands that Magic started with, or returns why they did not run.
    /// </summary>
    /// <remarks>
    /// Only open runs a project. Each other command does its job, and then Magic exits.
    /// </remarks>
    public Result Execute(string[] commands)
    {
        TakeGemCommands();
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
        string[] words = SplitWords(command);
        if (words.Length == 0)
            return Result.Success();

        TakeGemCommands();
        if (!words[0].StartsWith('-') && !_cliCommands.Subcommands.Any(subcommand => subcommand.Name == words[0]))
            words[0] = (words[0].Length == 1 ? "-" : "--") + words[0];

        return Invoke(_cliCommands.Parse(words));
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
    /// <paramref name="command"/> in its words: a space ends a word, and a pair of quotes holds one together and goes
    /// with it, as a shell does.
    /// </summary>
    /// <remarks>
    /// A word in single quotes keeps its double quotes, so a value in JSON comes through whole:
    /// <c>set sun.scriptcontainer '[{"id":100421}]'</c>. A pair of quotes alone is an empty word, and a doubled quote
    /// inside a quoted word is one quote of the word: <c>set player.name 'it''s'</c>.
    /// </remarks>
    private static string[] SplitWords(string command)
    {
        List<string> words = [];
        StringBuilder? word = null;
        char quote = default;
        for (int i = 0; i < command.Length; i++)
        {
            char letter = command[i];
            if (letter == quote && i + 1 < command.Length && command[i + 1] == quote)
            {
                // The second quote of the pair is the letter, so the word goes on past it.
                (word ??= new()).Append(letter);
                i++;
            }
            else if (letter == quote)
                quote = default;
            else if (quote == default && letter is '"' or '\'')
            {
                // The quote starts the word, so a pair that holds nothing is an empty word.
                quote = letter;
                word ??= new();
            }
            else if (quote == default && char.IsWhiteSpace(letter))
            {
                if (word is not null)
                    words.Add(word.ToString());

                word = null;
            }
            else
                (word ??= new()).Append(letter);
        }

        if (word is not null)
            words.Add(word.ToString());

        return [.. words];
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

    /// <summary>
    /// Puts the commands that gems registered since the last parse beside Magic's own, and their subjects of
    /// <c>query</c> beside Magic's.
    /// </summary>
    /// <remarks>
    /// The gems are made after the console, so a gem's command joins it here, before the line that uses it is parsed.
    /// </remarks>
    private void TakeGemCommands()
    {
        while (Commands.TryTake(out Command? command))
        {
            // A gem's command that has the name of one of Magic's takes its subcommands, as the save gem's save takes
            // the subcommand settings.
            Command? same = _cliCommands.Subcommands.FirstOrDefault(own => own.Name == command.Name);
            if (same is not null)
            {
                _cliCommands.Subcommands.Remove(same);
                foreach (Command own in same.Subcommands.ToArray())
                {
                    command.Subcommands.Add(own);
                }
            }

            _cliCommands.Subcommands.Add(command);
        }

        while (Commands.TryTakeQuery(out (string Name, string Description, Func<JsonObject> Read) added))
        {
            _query.Subcommands.Add(
                QueryCommand.CreateSubject(_files, _queryOutput, added.Name, added.Description, added.Read)
            );
        }
    }
}

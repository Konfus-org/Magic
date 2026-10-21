using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ConsoleCommands;

/// <summary>
/// Commands typed in the terminal Magic was started from, or piped into it. A thread of its own reads lines into a
/// channel, and <see cref="Update"/> runs them on the main thread, so a command touches the engine like any hook; a
/// console of another kind hands its lines to <see cref="Run"/>. Built in: <c>help</c>, <c>quit</c>, <c>wait</c>,
/// <c>open</c> and <c>screenshot</c>.
/// </summary>
internal sealed class ConsoleCommandsGem : IGem, ICommands
{
    /// <summary>
    /// The lines read from the terminal, the process's rather than a run's: a run started again
    /// (<see cref="Lifetime.Restart"/>) goes on from where the last left off, so what is piped in is one script for the
    /// whole process.
    /// </summary>
    private static readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new() { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// Whether the thread reading the terminal has been started, which the first gem made does.
    /// </summary>
    private static bool _isReading;

    /// <summary>
    /// Frames left before the lines after a <c>wait</c> run: the process's, as the lines are, so a wait goes on across
    /// a restart, the frames of the restart itself not counted.
    /// </summary>
    private static int _waiting;

    /// <summary>
    /// Whether the lines after a <c>wait loaded</c> are held back until the world has loaded: the process's, as the
    /// lines are, so the wait goes on across a restart until the restarted world has loaded.
    /// </summary>
    private static bool _isWaitingForWorld;

    private readonly SortedDictionary<string, Command> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFileSystem _files;
    private readonly World _world;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly CancellationToken _stopping;
    private readonly IGpu? _gpu;
    private readonly IWindow? _window;

    /// <summary>
    /// What an <c>open</c> is opening, the lines after it held back until it is done: this run's, as the world is, so a
    /// restart before it is done makes it a <c>wait loaded</c> for the next run (<see cref="Stop"/>).
    /// </summary>
    private Task? _opening;

    public ConsoleCommandsGem(
        Lifetime lifetime,
        Threads threads,
        IFileSystem files,
        World world,
        Assets assets,
        IGpu? gpu = null,
        IWindow? window = null)
    {
        _files = files;
        _world = world;
        _assets = assets;
        _threads = threads;
        _stopping = lifetime.Stopping;
        _gpu = gpu;
        _window = window;
        _commands.Add("help", new Command("Lists every command and what it does.", _ => Help()));
        _commands.Add("quit", new Command("Stops the run.", _ => lifetime.Stop("quit typed in the terminal")));
        _commands.Add("wait", new Command(
            "Holds the lines after it back for that many frames, or until the world has loaded: wait 100, wait loaded.",
            Wait));
        _commands.Add("open", new Command(
            "Opens a domain in place of the world, by its asset path, and holds the lines after it back until it is "
                + "there: open Domains/Hub/Hub.domain.",
            Open));
        _commands.Add(
            "screenshot",
            new Command("Saves the window's next image as Screenshots/<time>.png beside Magic.", _ => Screenshot()));

        // A thread of its own rather than a task, and a background one: a terminal read cannot be cancelled on every
        // OS, so the thread waits in it until a line comes or the process ends, through every run of the process.
        if (_isReading) return;

        _isReading = true;
        new Thread(ReadLines) { IsBackground = true, Name = "Terminal input" }.Start();
    }

    public Subscription Register(string name, string description, Action<ReadOnlySpan<string>> run)
    {
        if (!_commands.TryAdd(name, new Command(description, run)))
        {
            throw new ArgumentException($"There is already a command named {name}.", nameof(name));
        }

        return new Subscription(() => _commands.Remove(name));
    }

    public void Run(string line)
    {
        string[] words = Split(line);
        if (words.Length == 0) return;

        if (!_commands.TryGetValue(words[0], out Command? command))
        {
            Debugging.Warn($"There is no command {words[0]}; help lists them.");
            return;
        }

        command.Run(words.AsSpan(1));
    }

    public void Update(in Frame frame)
    {
        if (_waiting > 0)
        {
            _waiting--;
            return;
        }

        if (_isWaitingForWorld)
        {
            if (!_world.IsLoaded) return;

            _isWaitingForWorld = false;
        }

        while (_waiting == 0
            && !_isWaitingForWorld
            && _opening is not { IsCompleted: false }
            && _lines.Reader.TryRead(out string? line))
        {
            Run(line);
        }
    }

    public void Stop()
    {
        // A restart ends this run's open, and the next run opens the domain again as it puts the world back, so the
        // lines after it wait for that world as they would after a wait loaded.
        if (_opening is { IsCompleted: false }) _isWaitingForWorld = true;
    }

    /// <summary>
    /// The words of <paramref name="line"/>. Spaces part them, but not inside double quotes, braces or brackets, so
    /// <c>"two words"</c> is one word and <c>{"a": [1, 2]}</c> one that keeps its JSON. Quotes around a whole word are
    /// dropped, and those inside one kept.
    /// </summary>
    private static string[] Split(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool isQuoted = false;
        bool isWrapped = false;
        bool hasAny = false;
        int depth = 0;
        foreach (char character in line)
        {
            if (character == '"')
            {
                if (!hasAny)
                {
                    isWrapped = true;
                }
                else if (!isWrapped || !isQuoted)
                {
                    word.Append(character);
                }

                isQuoted = !isQuoted;
                hasAny = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !isQuoted && depth == 0)
            {
                if (hasAny) words.Add(word.ToString());

                word.Clear();
                hasAny = false;
                isWrapped = false;
                continue;
            }

            if (!isQuoted)
            {
                depth = Math.Max(0, depth + character switch { '{' or '[' => 1, '}' or ']' => -1, _ => 0 });
            }

            word.Append(character);
            hasAny = true;
        }

        if (hasAny) words.Add(word.ToString());

        return [.. words];
    }

    private static void ReadLines()
    {
        while (Console.ReadLine() is { } line)
        {
            // Input piped from some shells starts with a byte order mark.
            _lines.Writer.TryWrite(line.TrimStart('\uFEFF'));
        }
    }

    /// <summary>
    /// <c>help</c>: every command's name and what it does, a line each, in order.
    /// </summary>
    private void Help()
    {
        int width = 0;
        foreach (string name in _commands.Keys)
        {
            width = Math.Max(width, name.Length);
        }

        StringBuilder text = new("Commands:");
        foreach ((string name, Command command) in _commands)
        {
            text.Append("\n  ").Append(name.PadRight(width)).Append("  ").Append(command.Description);
        }

        Debugging.Info(text.ToString());
    }

    /// <summary>
    /// <c>wait 100</c>: holds the lines after it back for that many frames; <c>wait loaded</c>: until the world has
    /// loaded (<see cref="World.IsLoaded"/>), every open domain spawned, so a piped script takes its screenshot of the
    /// world rather than of the loading screen, however long the shaders take to compile.
    /// </summary>
    private void Wait(ReadOnlySpan<string> words)
    {
        if (words.Length == 1 && string.Equals(words[0], "loaded", StringComparison.OrdinalIgnoreCase))
        {
            _isWaitingForWorld = true;
            return;
        }

        if (words.Length != 1
            || !int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int frames))
        {
            Debugging.Warn("wait takes a number of frames, or loaded: wait 100, wait loaded");
            return;
        }

        _waiting = frames;
    }

    /// <summary>
    /// <c>open Domains/Hub/Hub.domain</c>: the domain at that asset path in place of the world, the lines after it held
    /// back until it is there. A <c>wait loaded</c> alone would let them through at once, as the world before still
    /// counts as loaded until the domain's file has been read.
    /// </summary>
    private void Open(ReadOnlySpan<string> words)
    {
        if (words.Length != 1)
        {
            Debugging.Warn("open takes a domain's asset path: open Domains/Hub/Hub.domain");
            return;
        }

        Handle<Domain> domain = _assets.Find<Domain>(words[0]);
        if (domain == Handle<Domain>.None)
        {
            Debugging.Error($"There is no domain at {words[0]}.");
            return;
        }

        _opening = OpenAsync(domain, words[0]);
        _opening.FireAndForget();
    }

    private async Task OpenAsync(Handle<Domain> domain, string path)
    {
        Result<Domain> opened = await _world.OpenAsync(domain, DomainOpenMode.Replace, cancel: _stopping)
            .ConfigureAwait(false);
        if (!opened.Ok) Debugging.Error($"{path} did not open: {opened.Error}");
    }

    /// <summary>
    /// <c>screenshot</c>: the window's next image, as it is shown, saved as Screenshots/&lt;time&gt;.png beside Magic.
    /// </summary>
    private void Screenshot()
    {
        if (_gpu is not { } gpu || _window is not { } window)
        {
            Debugging.Warn("There is no window to take a screenshot of.");
            return;
        }

        string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture) + ".png";
        SaveAsync(gpu, window, _files.Combine(AppContext.BaseDirectory, "Screenshots", name)).FireAndForget();
    }

    private async Task SaveAsync(IGpu gpu, IWindow window, string path)
    {
        Task<Result<GpuImage>> capture = await _threads.InvokeAsync(
            EngineThread.Render,
            () => gpu.CaptureAsync(gpu.GetWindowTarget(window)),
            _stopping).ConfigureAwait(false);
        Result<GpuImage> image = await capture.ConfigureAwait(false);
        if (!image.Ok)
        {
            Debugging.Error($"No screenshot was taken: {image.Error}");
            return;
        }

        Result written = await _files.WriteAsync(path, Png.Encode(image.Value), _stopping).ConfigureAwait(false);
        if (written.Ok)
        {
            Debugging.Info($"Screenshot: {path}");
        }
        else
        {
            Debugging.Error($"The screenshot was not saved: {written.Error}");
        }
    }

    /// <summary>
    /// One command: what <c>help</c> says of it, and what runs with the words typed after its name.
    /// </summary>
    private sealed record Command(string Description, Action<ReadOnlySpan<string>> Run);
}

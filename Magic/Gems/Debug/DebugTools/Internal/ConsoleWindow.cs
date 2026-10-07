using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Collections.Concurrent;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The console, opened with the grave key (or from the start, when the project says so): every log line (the last
/// <see cref="ConsoleLines"/>) in a scrolling view above a line to type in, whose first word names a
/// <see cref="IConsoleCommands"/> command (or <c>help</c> or <c>clear</c>) and the rest its arguments. Its gem is a
/// logger and hands it every line (<see cref="Add"/>) from the moment the gem is loaded. The terminal the host was started from is a
/// console too: a line typed there runs the same way, in this system's frame, so a headless run is driven from it.
/// </summary>
internal sealed class ConsoleWindow : Window
{
    public const int ConsoleLines = 10_000;

    private readonly Lock _logLock = new();
    private readonly Queue<string> _log = new();
    private readonly StringBuilder _builder = new();
    private readonly ConcurrentQueue<string> _typed = new(); // lines from the terminal, run in the next frame

    private string _line = "";
    private string _text = "";
    private bool _logChanged;

    public ConsoleWindow(IDebugWidgets ui, DebugUI debug, IInput? input, IClipboard? clipboard) : base("Console", Key.Grave, ui, debug, input, clipboard)
    {
        // Not one of the engine's threads (Threads.Create): this one sits in ReadLine until the terminal closes, and
        // Threads joins its own at dispose, which would wait for that. A long-running task gets a thread of its own
        // that goes with the process.
        Task.Factory.StartNew(ReadTerminal, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).FireAndForget();
    }

    protected override void Draw(in Frame frame)
    {
        while (_typed.TryDequeue(out string? typed))
            Execute(typed);

        if (!Open)
            return;

        Begin(scrollable: true);
        UI.Begin("Log", scrollable: true);
        string text = LogText();
        UI.Document("##log", ref text, readOnly: true);
        UI.End();

        if (UI.Input(">", ref _line))
        {
            string line = _line.Trim();
            _line = "";
            if (line.Length > 0)
                Execute(line);
        }
        UI.End();
    }

    /// <summary>
    /// Every line typed in the terminal, until it closes (end of input), kept for the next frame to run as if typed
    /// in the window: a command is main-thread work, and nothing runs before the frames do.
    /// </summary>
    private void ReadTerminal()
    {
        try
        {
#pragma warning disable RS0030 // the terminal is the console's to read
            while (Console.In.ReadLine() is { } line)
#pragma warning restore RS0030
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    _typed.Enqueue(trimmed);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // No terminal to read, or it went: the window is the console from here.
        }
    }

    /// <summary>
    /// The log as one text, rebuilt only when a line arrived since the last time.
    /// </summary>
    protected override string Contents()
    {
        return LogText();
    }

    private string LogText()
    {
        lock (_logLock)
        {
            if (!_logChanged)
                return _text;

            _builder.Clear();
            foreach (string line in _log)
                _builder.AppendLine(line);

            _text = _builder.ToString();
            _logChanged = false;
            return _text;
        }
    }

    /// <summary>
    /// Runs one typed line: <c>help</c> and <c>clear</c> are the console's own, anything else the command of that name.
    /// </summary>
    private void Execute(string line)
    {
        Log.Info($"> {line}");

        string[] words = Split(line);
        switch (words[0])
        {
            case "help":
                Log.Info($"Commands: {string.Join(", ", Debug.Commands.Append("clear").Append("help").Order())}");
                return;
            case "clear":
                lock (_logLock)
                {
                    _log.Clear();
                    _logChanged = true;
                }
                return;
        }

        if (!Debug.Run(words[0], words[1..]))
            Log.Warn($"Unknown command '{words[0]}'. Type help for the list.");
    }

    /// <summary>
    /// Whitespace separates arguments; double quotes, braces and brackets keep spaces inside one. Quotes around a whole
    /// argument are dropped, those inside one are kept, so <c>set Assets.Budgets={"Chunk": 512, "Texture": 256}</c>
    /// is one argument and keeps its JSON.
    /// </summary>
    private static string[] Split(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool quoted = false, wrapped = false, any = false;
        int depth = 0;

        foreach (char character in line)
        {
            if (character == '"')
            {
                if (!any)
                    wrapped = true;
                else if (!wrapped || !quoted)
                    word.Append(character);

                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted && depth == 0)
            {
                if (any)
                    words.Add(word.ToString());

                word.Clear();
                any = wrapped = false;
                continue;
            }

            if (!quoted)
                depth = Math.Max(0, depth + character switch { '{' or '[' => 1, '}' or ']' => -1, _ => 0 });

            word.Append(character);
            any = true;
        }

        if (any)
            words.Add(word.ToString());

        return [.. words];
    }

    /// <summary>
    /// A log line, from any thread.
    /// </summary>
    public void Add(LogLevel level, string message)
    {
        lock (_logLock)
        {
            _log.Enqueue(level >= LogLevel.Warning ? $"{level}: {message}" : message);
            while (_log.Count > ConsoleLines)
                _log.Dequeue();

            _logChanged = true;
        }
    }
}

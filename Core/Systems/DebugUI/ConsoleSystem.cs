using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Utils;
using System.Text;

namespace Magic.Systems.DebugUI;

/// <summary>
/// The console, opened with the grave key: every log line (the last <see cref="ConsoleLines"/>) in a scrolling view
/// above a line to type in, whose first word names a <see cref="Debugging.Commands"/> command (or <c>help</c> or
/// <c>clear</c>) and the rest its arguments. It is a logger itself, so it sees every line from the moment it exists.
/// </summary>
public sealed class ConsoleSystem : DebugWindowSystem, ILogger
{
    public const int ConsoleLines = 10_000;

    private readonly Lock _logLock = new();
    private readonly Queue<string> _log = new();
    private readonly StringBuilder _builder = new();

    private string _line = "";
    private string _text = "";
    private bool _logChanged;

    public ConsoleSystem() : base(Key.Grave)
    {
        Debugging.Log.Register(this);
    }

    protected override void Run(in Frame frame)
    {
        if (!Open)
            return;

        Debugging.UI.Begin("Console", scrollable: true);
        Debugging.UI.Begin("Log", scrollable: true);
        string text = LogText();
        Debugging.UI.Document("##log", ref text, readOnly: true);
        Debugging.UI.End();

        if (Debugging.UI.Input(">", ref _line))
        {
            string line = _line.Trim();
            _line = "";
            if (line.Length > 0)
                Execute(line);
        }
        Debugging.UI.End();
    }

    /// <summary>The log as one text, rebuilt only when a line arrived since the last time.</summary>
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

    /// <summary>Runs one typed line: <c>help</c> and <c>clear</c> are the console's own, anything else the command of that name.</summary>
    private void Execute(string line)
    {
        Debugging.Log.Info($"> {line}");

        string[] words = Split(line);
        switch (words[0])
        {
            case "help":
                Debugging.Log.Info($"Commands: {string.Join(", ", Debugging.Commands.Names.Append("clear").Append("help").Order())}");
                return;
            case "clear":
                lock (_logLock)
                {
                    _log.Clear();
                    _logChanged = true;
                }
                return;
        }

        if (!Debugging.Commands.Run(words[0], words[1..]))
            Debugging.Log.Warn($"Unknown command '{words[0]}'. Type help for the list.");
    }

    /// <summary>Whitespace separates arguments; double quotes keep spaces inside one.</summary>
    internal static string[] Split(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool quoted = false, any = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    words.Add(word.ToString());

                word.Clear();
                any = false;
                continue;
            }

            word.Append(c);
            any = true;
        }

        if (any)
            words.Add(word.ToString());

        return [.. words];
    }

    public void Log(LogLevel level, string message, string file, int line)
    {
        lock (_logLock)
        {
            _log.Enqueue(level >= LogLevel.Warning ? $"{level}: {message}" : message);
            while (_log.Count > ConsoleLines)
                _log.Dequeue();

            _logChanged = true;
        }
    }

    public void Flush()
    {
    }

    public override void Dispose()
    {
        Debugging.Log.Unregister(this);
        base.Dispose();
    }
}

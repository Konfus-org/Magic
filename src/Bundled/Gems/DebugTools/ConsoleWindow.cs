using System.Text;
using Magic.Contexts.Logging;
using Magic.Contexts.Subscriptions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The console: the log's last <see cref="KeptLines"/> lines in a scrolling view, and under them a line to type a
/// command on, run as if it were typed at the terminal (<see cref="ICommands.Run"/>). Its own command, <c>clear</c>,
/// empties the view. The log's lines arrive on the log's thread (<see cref="Add"/>), so they are locked.
/// </summary>
internal sealed class ConsoleWindow : IDisposable
{
    private const int KeptLines = 10_000;

    private readonly ToolWindow _window;
    private readonly IDebugUI _ui;
    private readonly ICommands? _commands;
    private readonly Subscription? _clearing;
    private readonly Lock _lock = new();
    private readonly Queue<string> _lines = new();

    /// <summary>
    /// The lines put together as the view's text, only when one came since.
    /// </summary>
    private readonly StringBuilder _joined = new();

    private string _typed = string.Empty;
    private string _text = string.Empty;
    private bool _isChanged;

    public ConsoleWindow(ToolWindow window, IDebugUI ui, ICommands? commands)
    {
        _window = window;
        _ui = ui;
        _commands = commands;
        _clearing = commands?.Register("clear", _ => Clear());
    }

    public void Dispose()
    {
        _clearing?.Dispose();
    }

    /// <summary>
    /// A line of the log, on the log's thread: its message, after its level when it is a warning or worse.
    /// </summary>
    public void Add(in LogLine line)
    {
        string shown = line.Level >= LogLevel.Warn ? $"{line.Level}: {line.Message}" : line.Message;
        lock (_lock)
        {
            _lines.Enqueue(shown);
            while (_lines.Count > KeptLines)
            {
                _lines.Dequeue();
            }

            _isChanged = true;
        }
    }

    public void Update()
    {
        if (!_window.Begin(scrollable: true))
        {
            return;
        }

        _ui.Begin("Log", scrollable: true);
        string text = Text();
        _ui.Document("##log", ref text, readOnly: true);
        _ui.End();
        if (_commands is { } commands && _ui.Input(">", ref _typed))
        {
            string line = _typed.Trim();
            _typed = string.Empty;
            if (line.Length > 0)
            {
                Debugging.Info($"> {line}");
                commands.Run(line);
            }
        }

        if (_window.IsCopyClicked)
        {
            _window.Copy(Text());
        }

        _window.End();
    }

    /// <summary>
    /// The lines as one text, put together again only when a line came since the last time.
    /// </summary>
    private string Text()
    {
        lock (_lock)
        {
            if (!_isChanged)
            {
                return _text;
            }

            _joined.Clear();
            foreach (string line in _lines)
            {
                _joined.Append(line).Append('\n');
            }

            _text = _joined.ToString();
            _isChanged = false;
            return _text;
        }
    }

    /// <summary>
    /// <c>clear</c>: empties the view; the log keeps its lines.
    /// </summary>
    private void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
            _isChanged = true;
        }
    }
}

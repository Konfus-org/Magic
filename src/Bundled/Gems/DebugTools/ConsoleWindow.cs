using System.Globalization;
using System.Linq;
using Magic.Contexts.Assets;
using Magic.Contexts.Logging;
using Magic.Contexts.Subscriptions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The console: a filter, then the newest <see cref="ShownLines"/> of the log's last <see cref="KeptLines"/> lines
/// that contain it, each with its time and in its level's colour, in a scrolling view, and under them a line to type a
/// command on, run as if it were typed at the terminal (<see cref="ICommands.Run"/>). Its own command, <c>clear</c>,
/// empties the view; Copy gives every kept line. The log's lines arrive on the log's thread (<see cref="Add"/>), so
/// they are locked.
/// </summary>
internal sealed class ConsoleWindow : IDisposable
{
    private const int KeptLines = 10_000;

    /// <summary>
    /// As many as are drawn, so a frame stays flat however long the log; the rest are reached by the filter, Copy, or
    /// the terminal.
    /// </summary>
    private const int ShownLines = 500;

    // Warnings and errors in the colours the reports on screen use.
    private static readonly Color _warning = new(1, 0.376f, 0);
    private static readonly Color _error = new(1, 0, 0);

    private readonly ToolWindow _window;
    private readonly IDebugUI _ui;
    private readonly ICommands? _commands;
    private readonly Subscription? _clearing;
    private readonly Lock _lock = new();
    private readonly Queue<Line> _lines = new();

    /// <summary>
    /// The lines drawn, the newest that contain the filter, gathered again when a line came or the filter changed.
    /// </summary>
    private readonly List<Line> _shown = [];

    private string _typed = string.Empty;
    private string _filter = string.Empty;
    private string _shownFilter = string.Empty;
    private bool _isChanged;

    public ConsoleWindow(ToolWindow window, IDebugUI ui, ICommands? commands)
    {
        _window = window;
        _ui = ui;
        _commands = commands;
        _clearing = commands?.Register("clear", "Empties the console's view; the log keeps its lines.", _ => Clear());
    }

    public void Dispose()
    {
        _clearing?.Dispose();
    }

    /// <summary>
    /// A line of the log, on the log's thread: its time and its message, after its level when it is a warning or worse.
    /// </summary>
    public void Add(in LogLine line)
    {
        string shown = line.Level >= LogLevel.Warn
            ? string.Create(CultureInfo.InvariantCulture, $"{line.Time:HH:mm:ss.fff} {line.Level}: {line.Message}")
            : string.Create(CultureInfo.InvariantCulture, $"{line.Time:HH:mm:ss.fff} {line.Message}");
        lock (_lock)
        {
            _lines.Enqueue(new Line(shown, line.Level));
            while (_lines.Count > KeptLines)
            {
                _lines.Dequeue();
            }

            _isChanged = true;
        }
    }

    public void Update()
    {
        if (!_window.Begin(scrollable: true, out bool copy))
        {
            return;
        }

        _ui.Input("##filter", ref _filter, hint: "Filter");
        _ui.Begin("Log", scrollable: true);
        foreach (Line line in Shown())
        {
            _ui.Label(line.Text, line.Level >= LogLevel.Error ? _error : line.Level == LogLevel.Warn ? _warning : null);
        }

        _ui.End();
        if (_commands is { } commands && _ui.Input(">", ref _typed, focus: true))
        {
            string line = _typed.Trim();
            _typed = string.Empty;
            if (line.Length > 0)
            {
                Debugging.Info($"> {line}");
                commands.Run(line);
            }
        }

        if (copy)
        {
            _window.Copy(AllText());
        }

        _window.End();
    }

    /// <summary>
    /// The lines drawn: the newest <see cref="ShownLines"/> that contain the filter, in any case, gathered again only
    /// when a line came or the filter changed since.
    /// </summary>
    private List<Line> Shown()
    {
        lock (_lock)
        {
            if (!_isChanged && string.Equals(_filter, _shownFilter, StringComparison.Ordinal))
            {
                return _shown;
            }

            _isChanged = false;
            _shownFilter = _filter;
            _shown.Clear();
            foreach (Line line in _lines)
            {
                if (_filter.Length == 0 || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    _shown.Add(line);
                }
            }

            if (_shown.Count > ShownLines)
            {
                _shown.RemoveRange(0, _shown.Count - ShownLines);
            }

            return _shown;
        }
    }

    /// <summary>
    /// Every kept line as one text, each ended, whatever the filter.
    /// </summary>
    private string AllText()
    {
        lock (_lock)
        {
            return _lines.Count == 0 ? string.Empty : string.Join('\n', _lines.Select(line => line.Text)) + '\n';
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

    /// <summary>
    /// A line as the console shows it, and the level that colours it.
    /// </summary>
    private readonly record struct Line(string Text, LogLevel Level);
}

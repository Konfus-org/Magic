using Magic.Contexts.Debug;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Numerics;

namespace Magic.Services;

/// <summary>
/// The service: what gems show the developer (<see cref="IDebugUI"/>), kept in <see cref="Entries"/>,
/// and the console commands they add (<see cref="IConsoleCommands"/>). It draws nothing: whatever debug tools are
/// loaded take what shows each frame (<see cref="Take"/>), mark their windows open (<see cref="Show"/>) and run what
/// is typed (<see cref="Run"/>).
/// </summary>
public sealed class DebugUI : IDebugUI, IConsoleCommands
{
    private readonly Lock _lock = new();
    private readonly HashSet<object> _shown = [];
    private readonly Dictionary<string, Action<string[]>> _commands = [];

    /// <summary>
    /// Whether a debug window is open (<see cref="Show"/>).
    /// </summary>
    public bool Visible
    {
        get
        {
            lock (_lock)
                return _shown.Count > 0;
        }
    }

    public bool Enabled =>
#if DEBUG
        true;
#else
        Visible;
#endif

    /// <summary>
    /// Every command's name, sorted.
    /// </summary>
    public IEnumerable<string> Commands => _commands.Keys.Order();

    /// <summary>
    /// What shows for a while: the list's lines and everything reported with a position.
    /// </summary>
    private DebugEntries Entries { get; } = new();

    /// <summary>
    /// The time <see cref="Entries"/> are reported and taken at, in seconds.
    /// </summary>
    private static double Now => Environment.TickCount64 / 1000d;

    public void Text(Vector3 position, string text, Color? color = null)
    {
        Entries.Report(text, color ?? Color.White, position, 0f, Now, counted: false);
    }

    public void Info(string text, float seconds = IDebugUI.ScreenSeconds, string file = "", int line = 0)
    {
        Entries.Report(text, Color.White, null, seconds, Now, counted: true);
        Log.Write(LogLevel.Information, text, file, line);
    }

    public void Warning(string text, Vector3? position = null, float seconds = IDebugUI.ScreenSeconds, string file = "", int line = 0)
    {
        if (Entries.Report($"!!! {text} !!!", Color.Orange, position, seconds, Now, counted: false))
            Log.Write(LogLevel.Warning, text, file, line);
    }

    public void Error(string text, Vector3? position = null, float seconds = IDebugUI.ScreenSeconds, string file = "", int line = 0)
    {
        if (Entries.Report($"!!! {text} !!!", Color.Red, position, seconds, Now, counted: false))
            Log.Write(LogLevel.Error, text, file, line);
    }

    /// <summary>
    /// Adds what shows now to <paramref name="into"/>, in the order it was first reported: the entries reported with a
    /// position (world text), or the ones without (the list's lines). What has had its time, and was not reported again
    /// since the last take, is gone after it.
    /// </summary>
    public void Take(bool positioned, List<DebugEntry> into)
    {
        Entries.Take(Now, positioned, into);
    }

    /// <summary>
    /// Marks <paramref name="window"/> open or closed; <see cref="Visible"/> while any is open.
    /// </summary>
    public void Show(object window, bool shown)
    {
        lock (_lock)
        {
            if (shown)
                _shown.Add(window);
            else
                _shown.Remove(window);
        }
    }

    public Subscription Add(string name, Action<string[]> run)
    {
        if (_commands.ContainsKey(name))
            Log.Warn($"Console command '{name}' is added again; the newer one runs.");

        _commands[name] = run;
        return new Subscription(() =>
        {
            // Only if it is still this one: a newer command of the name stays.
            if (_commands.TryGetValue(name, out Action<string[]>? current) && current == run)
                _commands.Remove(name);
        });
    }

    /// <summary>
    /// Runs the command called <paramref name="name"/>; false when there is none. A command that throws is logged.
    /// </summary>
    public bool Run(string name, string[] args)
    {
        if (!_commands.TryGetValue(name, out Action<string[]>? run))
            return false;

        try
        {
            run(args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Console command '{name}' threw. {ex}");
        }

        return true;
    }
}

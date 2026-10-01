using Magic.Interfaces;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Magic.Utils;

public static class Debugging
{
    private static readonly Lock _lock = new();
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queuedLogs = [];

    /// <summary>Messages below this level are dropped before any logger sees them. The command line sets it.</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>Whether <see cref="LogLevel.Verbose"/> messages are written. <c>--verbose</c> sets it.</summary>
    public static bool Verbose { get; set; }

    /// <summary>How many messages at <see cref="LogLevel.Error"/> or above were written, for the exit code to report.</summary>
    public static int Errors { get => Volatile.Read(ref field); private set; }

    public static void Assert(bool condition, string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (condition)
            return;

        Log.Write(LogLevel.Critical, message, file, line);
        Log.Flush(); // Always flush on crit so it is written even if the application crashes right after.
        Debug.Assert(condition, message);
    }

    public static class Log
    {
        /// <summary>For periodic or high-volume diagnostics (FPS, streaming); dropped unless <see cref="Debugging.Verbose"/>.</summary>
        public static void Verbose(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Verbose, message, file, line);
        }

        public static void Debug(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Debug, message, file, line);
        }

        public static void Info(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Information, message, file, line);
        }

        public static void Warn(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Warning, message, file, line);
        }

        public static void Error(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Error, message, file, line);
            Flush(); // Always flush on error so it is written even if the application crashes right after.
        }

        /// <summary>
        /// Writes every message queued while no logger was registered, then flushes all loggers.
        /// </summary>
        internal static void Flush()
        {
            lock (_lock)
            {
                // We are crashing a crit ALWAYS means a crash
                if (_queuedLogs.Any(queued => queued.Level == LogLevel.Critical))
                {
                    StringBuilder sb = new();
                    foreach ((LogLevel Level, string Message, string File, int Line) log in _queuedLogs)
                    {
                        string line = $"[{log.File}-{log.Line}][{log.Level}]: {log.Message}";
                        if (_loggers.Count == 0)
                        {
                            if (log.Level > LogLevel.Warning)
                                Console.Error.WriteLine(line);
                            else
                                Console.WriteLine(line);
                        }

                        sb.AppendLine(line);
                    }

                    // Beside the log files, never in whatever folder the host was started from. Written with System.IO
                    // directly, the one place that is allowed: a crash can come before any service exists.
                    Directory.CreateDirectory(Services.Project.Logs);
                    File.WriteAllText(Path.Combine(Services.Project.Logs, $"{DateTime.Today:yyyy-MM-dd}.crash"), sb.ToString());
                }

                // No loggers :(
                if (_loggers.Count == 0)
                    return;

                // Log using registered loggers and clear the queue
                foreach ((LogLevel level, string message, string file, int line) in _queuedLogs)
                {
                    foreach (ILogger logger in _loggers)
                    {
                        logger.Log(level, message, file, line);
                        logger.Flush();
                    }
                }
                _queuedLogs.Clear();
            }
        }

        internal static void Register(ILogger logger)
        {
            lock (_lock)
            {
                _loggers.Add(logger);
            }

            Flush();
        }

        internal static void Unregister(ILogger logger)
        {
            lock (_lock)
            {
                _loggers.Remove(logger);
            }
        }

        internal static void Write(LogLevel level, string message, string file, int line)
        {
            if (level == LogLevel.Verbose ? !Debugging.Verbose : level < MinimumLevel)
                return;

            lock (_lock)
            {
                if (level >= LogLevel.Error)
                    Errors++;

                // Nothing to write to yet (loggers come from gems): keep the message until one registers.
                if (_loggers.Count == 0)
                {
                    _queuedLogs.Add((level, message, file, line));
                    return;
                }

                foreach (ILogger logger in _loggers)
                    logger.Log(level, message, file, line);
            }
        }
    }

    /// <summary>
    /// Immediate-mode debug UI: a convenience over every <see cref="IDebugUI"/> the loaded gems export, the way
    /// <see cref="Log"/> is over the loggers. Draw every frame something should show, from the main thread, in Update
    /// or LateUpdate: <c>Begin("Stats")</c>, <c>Text(...)</c>, <c>if (Button("Reload")) ...</c>, <c>End()</c>.
    /// Nothing is kept here: the UI gem draws the calls as they come. While <see cref="Visible"/> is false (the debug
    /// UI is toggled off) every call does nothing and answers false.
    /// </summary>
    public static class UI
    {
        private static readonly List<IDebugUI> _uis = [];
        private static readonly HashSet<object> _shown = [];

        /// <summary>
        /// Whether the debug UI shows: while any debug window (<see cref="Systems.DebugUI.DebugWindowSystem"/>) is open.
        /// Skip building text while it is false.
        /// </summary>
        public static bool Visible => _shown.Count > 0;

        /// <summary>
        /// Opens a window titled <paramref name="title"/>; everything until <see cref="End"/> goes in it. Inside another it
        /// is a nested view. See <see cref="IDebugUI.Begin"/>.
        /// </summary>
        public static void Begin(string title, bool scrollable = false)
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.Begin(title, scrollable);
        }

        public static void End()
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.End();
        }

        public static void Text(string text)
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.Text(text);
        }

        /// <summary>True when the button was clicked.</summary>
        public static bool Button(string label)
        {
            if (!Visible)
                return false;

            bool clicked = false;
            foreach (IDebugUI ui in _uis)
                clicked |= ui.Button(label);

            return clicked;
        }

        /// <summary>A one-line text field editing <paramref name="text"/>; true when Enter was pressed in it.</summary>
        public static bool Input(string label, ref string text)
        {
            if (!Visible)
                return false;

            bool entered = false;
            foreach (IDebugUI ui in _uis)
                entered |= ui.Input(label, ref text);

            return entered;
        }

        /// <summary>Multi-line text editing <paramref name="text"/>; true when changed.</summary>
        public static bool Document(string label, ref string text, bool readOnly = false)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Document(label, ref text, readOnly);

            return changed;
        }

        /// <summary>True when changed.</summary>
        public static bool Checkbox(string label, ref bool value)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Checkbox(label, ref value);

            return changed;
        }

        /// <summary>A number within <paramref name="min"/>..<paramref name="max"/> (equal bounds: none); true when changed.</summary>
        public static bool Slider(string label, ref float value, float min = 0f, float max = 0f)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Slider(label, ref value, min, max);

            return changed;
        }

        /// <summary>One of <paramref name="options"/>, by index; true when changed.</summary>
        public static bool Choice(string label, ref int index, string[] options)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Choice(label, ref index, options);

            return changed;
        }

        internal static void Register(IDebugUI ui)
        {
            _uis.Add(ui);
        }

        internal static void Unregister(IDebugUI ui)
        {
            _uis.Remove(ui);
        }

        /// <summary>Marks <paramref name="window"/> open or closed; the debug UI shows while any is open.</summary>
        internal static void Show(object window, bool shown)
        {
            if (shown)
                _shown.Add(window);
            else
                _shown.Remove(window);
        }
    }

    /// <summary>
    /// Console commands by name: <see cref="Register"/> one and the console runs it when its name is typed, with the
    /// rest of the line as arguments. Dispose the handle to take it away again (a gem does in its Dispose). Main thread.
    /// </summary>
    public static class Commands
    {
        private static readonly Dictionary<string, Registration> _commands = [];

        /// <summary>Adds <paramref name="name"/>, replacing a command of that name, until the handle is disposed.</summary>
        public static IDisposable Register(string name, Action<string[]> run)
        {
            if (_commands.ContainsKey(name))
                Log.Warn($"Console command '{name}' is registered again; the newer one runs.");

            Registration registration = new(name, run);
            _commands[name] = registration;
            return registration;
        }

        /// <summary>Every registered name, sorted.</summary>
        internal static IEnumerable<string> Names => _commands.Keys.Order();

        /// <summary>Runs the command called <paramref name="name"/>; false when there is none. A command that throws is logged.</summary>
        internal static bool Run(string name, string[] args)
        {
            if (!_commands.TryGetValue(name, out Registration? registration))
                return false;

            try
            {
                registration.Run(args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Console command '{name}' threw. {ex}");
            }

            return true;
        }

        private sealed class Registration(string name, Action<string[]> run) : IDisposable
        {
            public Action<string[]> Run { get; } = run;

            public void Dispose()
            {
                // Only if it is still this one: a newer registration of the name stays.
                if (_commands.TryGetValue(name, out Registration? current) && current == this)
                    _commands.Remove(name);
            }
        }
    }
}

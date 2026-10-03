using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

#pragma warning disable RS0030 // works before any service exists: a crash is written with System.IO and the console directly

namespace Magic.Utils;

public static class Debugging
{
    private static readonly Lock _lock = new();
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queuedLogs = [];

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
        /// <summary>
        /// Seconds a message written with <c>onScreen: true</c> stays in the list down the left of the window, unless
        /// the call says otherwise. Longer leaves time to read it but lets a busy stretch fill the list.
        /// </summary>
        public const float ScreenSeconds = 5f;

        /// <summary>
        /// Messages below this level are dropped before any logger sees them. The command line sets it.
        /// </summary>
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        /// <summary>
        /// Whether <see cref="LogLevel.Verbose"/> messages are written. <c>--verbose</c> sets it.
        /// </summary>
        public static bool EnableVerbose { get; set; }

        /// <summary>
        /// How many messages at <see cref="LogLevel.Error"/> or above were written, for the exit code to report.
        /// </summary>
        public static int Errors { get => Volatile.Read(ref field); private set; }

        /// <summary>
        /// For periodic or high-volume diagnostics (FPS, streaming); dropped unless <see cref="EnableVerbose"/>.
        /// Like every level: <paramref name="onScreen"/> also shows the message in the list down the left of the
        /// window for <paramref name="seconds"/>, in its level's colour, where the same message logged again while
        /// it shows is one line with how often (<see cref="UI.Enabled"/> says when the list shows).
        /// </summary>
        public static void Verbose(string message, bool onScreen = false, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Verbose, message, file, line, onScreen, seconds);
        }

        public static void Debug(string message, bool onScreen = false, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Debug, message, file, line, onScreen, seconds);
        }

        public static void Info(string message, bool onScreen = false, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Information, message, file, line, onScreen, seconds);
        }

        public static void Warn(string message, bool onScreen = false, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Warning, message, file, line, onScreen, seconds);
        }

        public static void Error(string message, bool onScreen = false, float seconds = ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write(LogLevel.Error, message, file, line, onScreen, seconds);
            Flush(); // Always flush on error so it is written even if the application crashes right after.
        }

        /// <summary>
        /// Writes every message queued while no logger was registered, then flushes all loggers.
        /// </summary>
        public static void Flush()
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

        /// <summary>
        /// The colour a level's messages have on screen.
        /// </summary>
        private static Color ColorOf(LogLevel level)
        {
            return level switch
            {
                LogLevel.Verbose or LogLevel.Debug => Color.Gray,
                LogLevel.Warning => Color.Yellow,
                LogLevel.Error or LogLevel.Critical => Color.Red,
                _ => Color.White,
            };
        }

        public static void Register(ILogger logger)
        {
            lock (_lock)
            {
                _loggers.Add(logger);
            }

            Flush();
        }

        public static void Unregister(ILogger logger)
        {
            lock (_lock)
            {
                _loggers.Remove(logger);
            }
        }

        internal static void Write(LogLevel level, string message, string file, int line, bool onScreen = false, float seconds = ScreenSeconds)
        {
            if (level == LogLevel.Verbose ? !EnableVerbose : level < MinimumLevel)
                return;

            if (onScreen)
                UI.Entries.Report(message, ColorOf(level), null, seconds, UI.Now, counted: true);

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
    /// The widgets keep nothing here: the UI gem draws the calls as they come, and while <see cref="Visible"/> is
    /// false (the debug UI is toggled off) each does nothing and answers false. What is kept is what shows for a
    /// while without being drawn every frame: the on-screen log, <see cref="Warning"/>, <see cref="Error"/> and
    /// text in the world, which show while <see cref="Enabled"/>.
    /// </summary>
    public static class UI
    {
        /// <summary>
        /// Metres from a camera beyond which text anchored in the world is not shown in its view. Farther shows more of
        /// a scene's labels at once, on top of each other in the distance; nearer keeps the screen clear until the
        /// camera is at what the text is about.
        /// </summary>
        public const float TextDistance = 50f;

        private static readonly List<IDebugUI> _uis = [];
        private static readonly HashSet<object> _shown = [];

        /// <summary>
        /// Whether the debug UI shows: while any debug window (<see cref="Show"/>) is open.
        /// Skip building text while it is false.
        /// </summary>
        public static bool Visible => _shown.Count > 0;

        /// <summary>
        /// Whether the on-screen log, warnings, errors and text in the world show: always in a Debug build, in a
        /// Release build only while the debug UI is on (<see cref="Visible"/>). Logging never depends on it.
        /// </summary>
        public static bool Enabled =>
#if DEBUG
                true;
#else
                Visible;
#endif

        /// <summary>
        /// What shows for a while: the on-screen log lines and everything reported with a position. The debug UI
        /// systems take from it each frame and draw.
        /// </summary>
        public static DebugEntries Entries { get; } = new();

        /// <summary>
        /// The time <see cref="Entries"/> are reported and taken at, in seconds.
        /// </summary>
        public static double Now => Environment.TickCount64 / 1000d;

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

        /// <summary>
        /// <see cref="Begin(string, bool)"/> for a window with a close button, which sets <paramref name="visible"/> to
        /// false when clicked. Still call <see cref="End"/> that frame.
        /// </summary>
        public static void Begin(string title, ref bool visible, bool scrollable = false)
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.Begin(title, ref visible, scrollable);
        }

        public static void End()
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.End();
        }

        public static void Text(string text, Color? color = null)
        {
            if (!Visible)
                return;

            foreach (IDebugUI ui in _uis)
                ui.Text(text, color ?? Color.White);
        }

        /// <summary>
        /// Text anchored at a world position: shown in every view of the main window that has the position in front
        /// of its camera and within <see cref="TextDistance"/> metres of it, drawn over everything, while
        /// <see cref="Enabled"/>. Keep it to a few words, a quick "why": it sits over the scene among others like
        /// it, and the log is where the detail goes. Call it every frame the text should show.
        /// </summary>
        public static void Text(Vector3 position, string text, Color? color = null)
        {
            Entries.Report(text, color ?? Color.White, position, 0f, Now, counted: false);
        }

        /// <summary>
        /// An explicit warning on screen, as <c>!!! text !!!</c> in orange: at <paramref name="position"/> in the world
        /// (see <see cref="Text(Vector3, string, Color?)"/>), or without one in the list down the left of the window.
        /// It stays for <paramref name="seconds"/> after it was last reported (0: only while reported every frame),
        /// and is logged as a warning when it appears, not again while it stays up. A few words.
        /// </summary>
        public static void Warning(string text, Vector3? position = null, float seconds = Log.ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (Entries.Report($"!!! {text} !!!", Color.Orange, position, seconds, Now, counted: false))
                Log.Write(LogLevel.Warning, text, file, line);
        }

        /// <summary>
        /// <see cref="Warning"/> for an error: red, and logged as an error.
        /// </summary>
        public static void Error(string text, Vector3? position = null, float seconds = Log.ScreenSeconds, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (Entries.Report($"!!! {text} !!!", Color.Red, position, seconds, Now, counted: false))
                Log.Write(LogLevel.Error, text, file, line);
        }

        /// <summary>
        /// True when the button was clicked.
        /// </summary>
        public static bool Button(string label)
        {
            if (!Visible)
                return false;

            bool clicked = false;
            foreach (IDebugUI ui in _uis)
                clicked |= ui.Button(label);

            return clicked;
        }

        /// <summary>
        /// A one-line text field editing <paramref name="text"/>; true when Enter was pressed in it.
        /// </summary>
        public static bool Input(string label, ref string text)
        {
            if (!Visible)
                return false;

            bool entered = false;
            foreach (IDebugUI ui in _uis)
                entered |= ui.Input(label, ref text);

            return entered;
        }

        /// <summary>
        /// Multi-line text editing <paramref name="text"/>; true when changed.
        /// </summary>
        public static bool Document(string label, ref string text, bool readOnly = false)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Document(label, ref text, readOnly);

            return changed;
        }

        /// <summary>
        /// True when changed.
        /// </summary>
        public static bool Checkbox(string label, ref bool value)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Checkbox(label, ref value);

            return changed;
        }

        /// <summary>
        /// A number within <paramref name="min"/>..<paramref name="max"/> (equal bounds: none); true when changed.
        /// </summary>
        public static bool Slider(string label, ref float value, float min = 0f, float max = 0f)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Slider(label, ref value, min, max);

            return changed;
        }

        /// <summary>
        /// One of <paramref name="options"/>, by index; true when changed.
        /// </summary>
        public static bool Choice(string label, ref int index, string[] options)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Choice(label, ref index, options);

            return changed;
        }

        /// <summary>
        /// A row of tabs, one of <paramref name="options"/> selected by index; what follows is the selected tab's
        /// content. True when changed.
        /// </summary>
        public static bool Tabs(string label, ref int index, string[] options)
        {
            if (!Visible)
                return false;

            bool changed = false;
            foreach (IDebugUI ui in _uis)
                changed |= ui.Tabs(label, ref index, options);

            return changed;
        }

        /// <summary>
        /// An item of the context menu a right-click in the window opens; true the frame it is clicked.
        /// </summary>
        public static bool MenuItem(string label)
        {
            if (!Visible)
                return false;

            bool clicked = false;
            foreach (IDebugUI ui in _uis)
                clicked |= ui.MenuItem(label);

            return clicked;
        }

        /// <summary>
        /// Text centred on a pixel of the main window: what the 3D debug UI system makes of text in the world.
        /// </summary>
        public static void Text(Vector2 pixel, string text, Color color)
        {
            foreach (IDebugUI ui in _uis)
                ui.Text(pixel, text, color);
        }

        /// <summary>
        /// The list down the left of the main window, top to bottom.
        /// </summary>
        public static void Lines(ReadOnlySpan<DebugLine> lines)
        {
            foreach (IDebugUI ui in _uis)
                ui.Lines(lines);
        }

        public static void Register(IDebugUI ui)
        {
            _uis.Add(ui);
        }

        public static void Unregister(IDebugUI ui)
        {
            _uis.Remove(ui);
        }

        /// <summary>
        /// Marks <paramref name="window"/> open or closed; the debug UI shows while any is open.
        /// </summary>
        public static void Show(object window, bool shown)
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
        private static readonly Dictionary<string, Action<string[]>> _commands = [];

        /// <summary>
        /// Adds <paramref name="name"/>, replacing a command of that name, until the handle is disposed.
        /// </summary>
        public static IDisposable Register(string name, Action<string[]> run)
        {
            if (_commands.ContainsKey(name))
                Log.Warn($"Console command '{name}' is registered again; the newer one runs.");

            _commands[name] = run;
            return new Subscription(() =>
            {
                // Only if it is still this one: a newer registration of the name stays.
                if (_commands.TryGetValue(name, out Action<string[]>? current) && current == run)
                    _commands.Remove(name);
            });
        }

        /// <summary>
        /// Every registered name, sorted.
        /// </summary>
        public static IEnumerable<string> Names => _commands.Keys.Order();

        /// <summary>
        /// Runs the command called <paramref name="name"/>; false when there is none. A command that throws is logged.
        /// </summary>
        public static bool Run(string name, string[] args)
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

    /// <summary>
    /// The engine's numbers in one place: whatever a system wants shown is <see cref="Set"/> under a dotted name
    /// (<c>Render.Draws</c>, <c>Streaming.Loaded</c>, <c>Pool.Texture.Bytes</c>), from any thread, as often as it
    /// changes; the debug display takes them all and prints them, grouped by the first part. Nothing here is
    /// computed: a rate or an average is the caller's to make before setting it.
    /// </summary>
    public static class Stats
    {
        private static readonly Lock _statsLock = new();
        private static readonly Dictionary<string, double> _values = [];

        public static void Set(string name, double value)
        {
            lock (_statsLock)
                _values[name] = value;
        }

        /// <summary>
        /// The last value set under <paramref name="name"/>; 0 when none was.
        /// </summary>
        public static double Get(string name)
        {
            lock (_statsLock)
                return _values.GetValueOrDefault(name);
        }

        /// <summary>
        /// Every value set, name-ordered, added to <paramref name="into"/>.
        /// </summary>
        public static void Take(List<(string Name, double Value)> into)
        {
            lock (_statsLock)
            {
                foreach ((string name, double value) in _values)
                    into.Add((name, value));
            }

            into.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        }
    }

    /// <summary>
    /// Screenshots that carry the world they were taken in: <see cref="Capture"/> writes which domains were open and
    /// where every named camera was into the PNG itself, as a text chunk any image viewer ignores, and
    /// <see cref="Restore"/> reads that back out of the file and puts the world there again.
    /// </summary>
    public static class Screenshot
    {
        /// <summary>
        /// The PNG text chunk the state is under.
        /// </summary>
        private const string Keyword = "Magic";

        private static readonly JsonSerializerOptions _json = new(AssetJson.Options) { WriteIndented = false, IgnoreReadOnlyProperties = true }; // no Matrix, IsValid or IsIdentity: only what is set back

        /// <summary>
        /// What <paramref name="window"/> last showed, before anything drawn over it, as a PNG at
        /// <paramref name="path"/> that carries the world's state. Waits for the GPU. Failed, with why, when nothing
        /// has been shown in the window yet or the file could not be written. Without <paramref name="ecs"/> no
        /// camera is saved. Render thread, while the main thread waits for it.
        /// </summary>
        public static Result Capture(IFileSystem files, IRendering rendering, IWindow window, World world, IEcs? ecs, string path)
        {
            Result<CapturedFrame> taken = rendering.Read(GpuTexture.Window(window.Handle));
            if (taken.Failed)
                return Result.Failure(taken.Message);

            State state = new()
            {
                Domains = [.. world.Active.Select(open => open.Domain).Where(domain => domain != world.Loading)], // the loading domain is not the world
                Cameras = ecs is null ? [] : CamerasOf(ecs),
            };

            CapturedFrame frame = taken.Payload;
            return files.WriteBinary(path, frame.Pixels.AsSpan().Png(frame.Width, frame.Height, (Keyword, JsonSerializer.Serialize(state, _json))));
        }

        /// <summary>
        /// Puts the world back as it was when the screenshot at <paramref name="path"/> was taken: its domains are
        /// opened in place of whatever is open (they are left alone when they are the ones open already), and once
        /// they are loaded every camera saved is put where it was. Failed, with why, when the file cannot be read,
        /// carries no state or a domain cannot be opened. Without <paramref name="ecs"/> no camera is moved.
        /// Main thread.
        /// </summary>
        public static Result Restore(IFileSystem files, World world, Events events, IEcs? ecs, string path)
        {
            Result<byte[]> read = files.ReadBinary(path);
            if (read.Failed)
                return Result.Failure(read.Message);

            if (read.Payload.AsSpan().PngText(Keyword) is not { } json)
                return Result.Failure($"{path} carries no world state: it is not a screenshot this engine took.");

            State? state;
            try
            {
                state = JsonSerializer.Deserialize<State>(json, _json);
            }
            catch (JsonException ex)
            {
                return Result.Failure($"The world state in {path} cannot be read: {ex.Message}");
            }

            if (state is null)
                return Result.Failure($"The world state in {path} is empty.");

            Handle<Domain>[] domains = state.Domains;
            if (!domains.SequenceEqual(world.Active.Select(open => open.Domain).Where(domain => domain != world.Loading)))
            {
                Result opened = world.Open(domains.Length > 0 ? domains[0] : Handle<Domain>.None);
                for (int index = 1; index < domains.Length && opened.Ok; index++)
                    opened = world.Open(domains[index], OpenMode.Additive);

                if (opened.Failed)
                    return opened;
            }

            if (ecs is null || state.Cameras.Length == 0)
                return Result.Success();

            if (domains.All(domain => world.StateOf(domain) == DomainState.Loaded))
            {
                Place(ecs, state.Cameras);
                return Result.Success();
            }

            // The cameras come with the domains: wait for the last of those. One closed in the meantime is not waited for.
            IDisposable? watch = null;
#pragma warning disable CA2000 // the watch disposes itself once the domains are there
            watch = events.Watch(EventType.DomainLoaded, _ =>
            {
                if (domains.Any(domain => world.StateOf(domain) == DomainState.Loading))
                    return;

                watch?.Dispose();
                Place(ecs, state.Cameras);
            });
#pragma warning restore CA2000

            return Result.Success();
        }

        /// <summary>
        /// Every camera that can be found again, by its path; one with a nameless entity above it, or itself nameless, cannot.
        /// </summary>
        private static CameraState[] CamerasOf(IEcs ecs)
        {
            List<CameraState> cameras = [];
            using IEcsQuery<Transform, Camera> query = ecs.Query<Transform, Camera>().Build();
            query.Each((Handle entity, ref Transform transform, ref Camera _) =>
            {
                if (PathOf(ecs, entity) is { } path)
                    cameras.Add(new CameraState(path, transform));
                else
                    Log.Debug($"Camera {entity} has no name, so the screenshot does not carry where it is.");
            });

            return [.. cameras];
        }

        /// <summary>
        /// The names from the root down to <paramref name="entity"/>, as <see cref="IEcs.Lookup"/> takes them; null when one is missing.
        /// </summary>
        private static string? PathOf(IEcs ecs, Handle entity)
        {
            string? path = null;
            for (Handle at = entity; at.IsValid; at = ecs.GetParent(at))
            {
                if (ecs.GetName(at) is not { } name)
                    return null;

                path = path is null ? name : $"{name}.{path}";
            }

            return path;
        }

        private static void Place(IEcs ecs, CameraState[] cameras)
        {
            foreach (CameraState camera in cameras)
            {
                Handle entity = ecs.Lookup(camera.Path);
                if (!entity.IsValid || !ecs.Has<Camera>(entity))
                {
                    Log.Warn($"Camera {camera.Path} of the screenshot is not in the world; it is not restored.");
                    continue;
                }

                ecs.Set(entity, camera.Transform);
            }
        }

        /// <summary>
        /// What a screenshot carries, as JSON: the open domains in the order they were opened, and the cameras.
        /// </summary>
        private sealed class State
        {
            public Handle<Domain>[] Domains { get; set; } = [];

            public CameraState[] Cameras { get; set; } = [];
        }

        /// <summary>
        /// A camera by its entity's path, with its transform relative to its parent.
        /// </summary>
        private readonly record struct CameraState(string Path, Transform Transform);
    }
}

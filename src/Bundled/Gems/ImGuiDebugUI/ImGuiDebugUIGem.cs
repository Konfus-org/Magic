using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Unicode;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The debug UI, drawn with Dear ImGui: what gems and scripts report, as lines down the left of the window and text in
/// the world, and the windows gems draw with its widgets (<see cref="IDebugUI"/>).
/// <para>
/// ImGui's frame is the main thread's. <see cref="Update"/> hands ImGui the frame's input and starts its frame, so the
/// widgets gems made after this one call in their Update go into it; <see cref="LateUpdate"/> draws what is reported,
/// ends the frame and copies what ImGui drew (<see cref="DrawLists"/>); <see cref="Render"/> draws that copy over the
/// window (<see cref="Overlay"/>), after the gems made after this one have drawn, so it is on top of what they drew.
/// Reports come from any thread and are kept until they have shown (<see cref="Reports"/>).
/// </para>
/// </summary>
internal sealed class ImGuiDebugUIGem : IGem, IDebugUI, IDisposable
{
    private const int InputBytes = 256;
    private const string ContextMenu = "##context";

    /// <summary>
    /// ImGui's -FLT_MIN width: up to the right edge of whatever the field is in.
    /// </summary>
    private const float Stretch = -1.17549435E-38f;

    /// <summary>
    /// Text in the world sits on a dark plate, so it reads over anything behind it.
    /// </summary>
    private const uint Plate = 0xC0000000;

    /// <summary>
    /// The lines on screen have no plate: a dark copy a pixel down and right keeps them readable over a bright scene.
    /// </summary>
    private const uint Shadow = 0xFF000000;

    private static readonly Vector2 _platePadding = new(4, 2);
    private static readonly Vector2 _linesMargin = new(8, 8);

    // What reports are written in, linear as Color is: white, an orange warning and a red error.
    private static readonly Color _white = new(1, 1, 1);
    private static readonly Color _warning = new(1, 0.376f, 0);
    private static readonly Color _error = new(1, 0, 0);

    /// <summary>
    /// Whether what is reported shows whatever windows are open, as in a Debug build, or only while one is.
    /// </summary>
#if DEBUG
    private const bool IsAlwaysShowing = true;
#else
    private const bool IsAlwaysShowing = false;
#endif

    private readonly ImGuiDebugUISettings _settings;
    private readonly IWindow _window;
    private readonly IInput? _input;
    private readonly ImGuiContextPtr _context;
    private readonly Reports _reports = new();
    private readonly DrawLists _drawLists = new();
    private readonly Overlay _overlay;
    private readonly CameraViews _views;
    private readonly Subscription? _viewing;
    private readonly List<ShownReport> _world = [];
    private readonly List<ShownReport> _lines = [];

    /// <summary>
    /// The one-line field's text as the NUL-terminated UTF-8 ImGui edits.
    /// </summary>
    private readonly byte[] _typed = new byte[InputBytes];

    /// <summary>
    /// What Begin opened, by depth, reused frame to frame.
    /// </summary>
    private readonly List<Level> _levels = [];

    /// <summary>
    /// By a nested view's id, the height of what followed it in its parent last frame.
    /// </summary>
    private readonly Dictionary<string, float> _reserved = [];

    /// <summary>
    /// By a document's id, its text as ImGui edits it, encoded once.
    /// </summary>
    private readonly Dictionary<string, DocumentBuffer> _documents = [];

    /// <summary>
    /// The windows given their first place, each a step down and right of the last.
    /// </summary>
    private readonly HashSet<string> _placed = [];

    /// <summary>
    /// By a row of tabs' id, the tab ImGui showed last frame.
    /// </summary>
    private readonly Dictionary<string, int> _tabs = [];

    /// <summary>
    /// By a field's label, what it shows and its hidden ImGui id, made once rather than every frame.
    /// </summary>
    private readonly Dictionary<string, (string Shown, string Id)> _fields = [];

    /// <summary>
    /// The ids of what is nested in a window, <c>parent/child</c>, made once rather than every frame.
    /// </summary>
    private readonly Dictionary<(string Parent, string Child), string> _paths = [];

    /// <summary>
    /// This gem, for ImGui's clipboard callbacks.
    /// </summary>
    private readonly GCHandle _self;

    /// <summary>
    /// The clipboard's text as the NUL-terminated UTF-8 ImGui reads, pinned until it asks again.
    /// </summary>
    private GCHandle _pasted;

    private FontSource? _font;
    private int _depth;
    private int _windowsDrawn;
    private bool _isInFrame;
    private bool _hasLoggedUnended;
    private volatile bool _isShowing;

    public unsafe ImGuiDebugUIGem(ImGuiDebugUISettings settings, IGpu gpu, IShaderCompiler compiler, Assets assets, IWindow window, IFileSystem files, Threads threads, Lifetime lifetime, IInput? input = null, World? world = null)
    {
        _settings = settings;
        _window = window;
        _input = input;

        // The bindings load cimgui themselves, by a path: the machine's, found from the gem's dependencies as its load
        // context finds every other native library.
        string library = new AssemblyDependencyResolver(typeof(ImGuiDebugUIGem).Assembly.Location).ResolveUnmanagedDllToPath("cimgui")
            ?? throw new InvalidOperationException("The gem carries no cimgui for this machine.");
        LibraryLoader.CustomLoadFolders.Add(files.Parent(library));

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;
        ImGuiStylePtr style = ImGui.GetStyle();
        Look.Apply(style);
        style.FontSizeBase = settings.FontSize;

        _self = GCHandle.Alloc(this);
        ImGuiPlatformIOPtr platform = ImGui.GetPlatformIO();
        platform.PlatformClipboardUserData = (void*)GCHandle.ToIntPtr(_self);
        platform.PlatformSetClipboardTextFn = (delegate* unmanaged[Cdecl]<ImGuiContext*, byte*, void>)&SetClipboard;
        platform.PlatformGetClipboardTextFn = (delegate* unmanaged[Cdecl]<ImGuiContext*, byte*>)&GetClipboard;

        _overlay = new Overlay(gpu, window, compiler, assets, threads, lifetime.Stopping);
        _views = new CameraViews(window);
        _viewing = world?.Add(_views);
        _isShowing = IsAlwaysShowing && window.Width > 0;
        LoadFontAsync(assets, threads, lifetime.Stopping).FireAndForget();
    }

    public void Dispose()
    {
        _viewing?.Dispose();
        _overlay.Dispose();
        ImGui.DestroyContext(_context);
        _font?.Dispose();
        _self.Free();
        if (_pasted.IsAllocated)
        {
            _pasted.Free();
        }
    }

    public bool Enabled => _isShowing;

    /// <summary>
    /// Hands ImGui the frame's input and starts its frame, which the widgets called in Update go into.
    /// </summary>
    public void Update(in Frame frame)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (_input is { } input)
        {
            ImGuiInput.Feed(io, input);
        }

        // Laid out in window pixels: the font size says how big the UI is.
        io.DisplaySize = new Vector2(_window.Width, _window.Height);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = Math.Max(frame.DeltaSeconds, 1e-4f);
        ImGui.NewFrame();
        _isInFrame = true;
        _windowsDrawn = 0;
    }

    /// <summary>
    /// Draws what is reported under the frame's windows, ends ImGui's frame, and copies what it drew for Render.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        if (_depth > 0 && !_hasLoggedUnended)
        {
            Debugging.Error("A debug UI window was begun and not ended; it is ended for it.");
            _hasLoggedUnended = true;
        }

        while (_depth > 0)
        {
            End();
        }

        _isShowing = _window.Width > 0 && (IsAlwaysShowing || _windowsDrawn > 0);
        _world.Clear();
        _lines.Clear();
        _reports.Take(Now(), _world, _lines);
        if (_isShowing)
        {
            DrawWorld();
            DrawLines();
        }

        ImGui.Render();
        _isInFrame = false;
        _drawLists.Copy(ImGui.GetDrawData(), _window.Width, _window.Height);
    }

    public void Render(in Frame frame)
    {
        _overlay.Draw(_drawLists);
    }

    public void Text(Vector3 position, string text, Color? color = null, string? tooltip = null)
    {
        _reports.Add(text, ReportKind.Plain, color ?? _white, position, tooltip, 0, Now());
    }

    public void Text(string text, Color? color = null)
    {
        _reports.Add(text, ReportKind.Plain, color ?? _white, null, null, 0, Now());
    }

    public void Info(string text, float? seconds = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _reports.Add(text, ReportKind.Counted, _white, null, null, seconds ?? _settings.ReportSeconds, Now());
        Debugging.Info(text, member, file, line);
    }

    public void Warn(string text, Vector3? position = null, float? seconds = null, string? tooltip = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_reports.Add(text, ReportKind.Alarm, _warning, position, tooltip, seconds ?? _settings.ReportSeconds, Now()))
        {
            Debugging.Warn(text, member, file, line);
        }
    }

    public void Error(string text, Vector3? position = null, float? seconds = null, string? tooltip = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_reports.Add(text, ReportKind.Alarm, _error, position, tooltip, seconds ?? _settings.ReportSeconds, Now()))
        {
            Debugging.Error(text, member, file, line);
        }
    }

    public unsafe void Begin(string title, bool scrollable = false)
    {
        Begin(title, null, scrollable);
    }

    public unsafe void Begin(string title, ref bool visible, bool scrollable = false)
    {
        fixed (bool* closable = &visible)
        {
            Begin(title, closable, scrollable);
        }
    }

    public void End()
    {
        if (_depth == 0)
        {
            return;
        }

        Level level = _levels[--_depth];

        // What followed each view nested in this one, measured now that everything after them is laid out.
        float end = ImGui.GetCursorPosY();
        foreach ((string id, float closedAt) in level.Closed)
        {
            _reserved[id] = end - closedAt;
        }

        if (!level.IsNested)
        {
            ImGui.End();
            return;
        }

        // Scrolled to the bottom: it stays there as what is in it grows.
        if (level.IsScrollable && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f)
        {
            ImGui.SetScrollHereY(1f);
        }

        ImGui.EndChild();
        _levels[_depth - 1].Closed.Add((level.Id, ImGui.GetCursorPosY()));
    }

    public void Label(string text, Color? color = null)
    {
        ThrowIfOutsideFrame();
        if (color is not { } tint)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Packed(tint));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public bool Button(string label)
    {
        ThrowIfOutsideFrame();
        return ImGui.Button(label);
    }

    public unsafe bool Input(string label, ref string text)
    {
        ThrowIfOutsideFrame();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        // Text too long for the field is cut, not an error.
        Utf8.FromUtf16(text, _typed.AsSpan(0, InputBytes - 1), out _, out int written);
        _typed[written] = 0;

        string id = LabelField(label);
        bool isEntered;
        fixed (byte* buffer = _typed)
        {
            isEntered = ImGui.InputText(id, buffer, InputBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (isEntered || ImGui.IsItemEdited())
        {
            text = Encoding.UTF8.GetString(_typed, 0, Array.IndexOf(_typed, (byte)0));
        }

        if (isEntered)
        {
            // The field keeps the keyboard, to type the next line.
            ImGui.SetKeyboardFocusHere(-1);
        }

        return isEntered;
    }

    public unsafe bool Document(string label, ref string text, bool readOnly = false)
    {
        ThrowIfOutsideFrame();
        string id = LabelField(label);
        if (!_documents.TryGetValue(id, out DocumentBuffer? document) || !ReferenceEquals(document.Text, text) || document.IsReadOnly != readOnly)
        {
            document = new DocumentBuffer(text, readOnly);
            _documents[id] = document;
        }

        float height = (document.Lines * ImGui.GetTextLineHeight()) + (ImGui.GetStyle().FramePadding.Y * 2);
        ImGuiInputTextFlags flags = readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;
        bool isChanged;
        fixed (byte* buffer = document.Bytes)
        {
            isChanged = ImGui.InputTextMultiline(id, buffer, (nuint)document.Bytes.Length, new Vector2(Stretch, height), flags);
        }

        if (!isChanged)
        {
            return false;
        }

        text = Encoding.UTF8.GetString(document.Bytes, 0, Array.IndexOf(document.Bytes, (byte)0));
        _documents[id] = new DocumentBuffer(text, readOnly);
        return true;
    }

    public bool Checkbox(string label, ref bool value)
    {
        ThrowIfOutsideFrame();
        return ImGui.Checkbox(LabelField(label), ref value);
    }

    public bool Slider(string label, ref float value, float min = 0f, float max = 0f)
    {
        ThrowIfOutsideFrame();
        float speed = Math.Max(Math.Abs(value) * 0.01f, 0.1f);
        return ImGui.DragFloat(LabelField(label), ref value, speed, min, max, "%.6g");
    }

    public bool Choice(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        string id = LabelField(label);
        if (!ImGui.BeginCombo(id, index >= 0 && index < options.Length ? options[index] : string.Empty))
        {
            return false;
        }

        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            if (ImGui.Selectable(options[i], i == index))
            {
                chosen = i;
            }
        }

        ImGui.EndCombo();
        if (chosen == index)
        {
            return false;
        }

        index = chosen;
        return true;
    }

    public bool Tabs(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        if (!ImGui.BeginTabBar(label))
        {
            return false;
        }

        // ImGui keeps its own selection: the caller's index is pushed only when it is not the tab ImGui last showed.
        string id = _depth > 0 ? PathOf(_levels[_depth - 1].Id, label) : label;
        int shown = _tabs.GetValueOrDefault(id, -1);
        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            ImGuiTabItemFlags flags = i == index && index != shown ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (!ImGui.BeginTabItem(options[i], flags))
            {
                continue;
            }

            chosen = i;
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        _tabs[id] = chosen;
        if (chosen == index)
        {
            return false;
        }

        index = chosen;
        return true;
    }

    public bool Header(string label)
    {
        ThrowIfOutsideFrame();
        return ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
    }

    public void Tooltip(string text)
    {
        ThrowIfOutsideFrame();
        if (ImGui.BeginItemTooltip())
        {
            WriteTooltip(text);
        }
    }

    public bool MenuItem(string label)
    {
        ThrowIfOutsideFrame();

        // Over the window's own items and nested views too (a document is one), not only its empty space.
        const ImGuiHoveredFlags Over = ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.IsWindowHovered(Over))
        {
            ImGui.OpenPopup(ContextMenu);
        }

        if (!ImGui.BeginPopup(ContextMenu))
        {
            return false;
        }

        bool isClicked = ImGui.MenuItem(label);
        ImGui.EndPopup();
        return isClicked;
    }

    /// <summary>
    /// The time reports are kept by, in seconds.
    /// </summary>
    private static double Now()
    {
        return Environment.TickCount64 / 1000d;
    }

    /// <summary>
    /// A linear colour as ImGui packs one, encoded to sRGB as the window's image wants: alpha, blue, green, red from the
    /// top byte down.
    /// </summary>
    private static uint Packed(Color color)
    {
        return (Byte(color.A) << 24) | (Byte(Encode(color.B)) << 16) | (Byte(Encode(color.G)) << 8) | Byte(Encode(color.R));

        static float Encode(float linear) => linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;

        static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>
    /// Lays out a field: its label, up to any <c>##</c>, on the left, then the field stretched to the right edge. Returns
    /// the field's hidden ImGui id: the whole label, so fields that share what they show stay apart.
    /// </summary>
    private string LabelField(string label)
    {
        if (!_fields.TryGetValue(label, out (string Shown, string Id) field))
        {
            int hidden = label.IndexOf("##", StringComparison.Ordinal);
            field = (hidden < 0 ? label : label[..hidden], "##" + label);
            _fields[label] = field;
        }

        if (field.Shown.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(field.Shown);
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(Stretch);
        return field.Id;
    }

    /// <summary>
    /// <paramref name="child"/>'s id in <paramref name="parent"/>, <c>parent/child</c>.
    /// </summary>
    private string PathOf(string parent, string child)
    {
        if (!_paths.TryGetValue((parent, child), out string? path))
        {
            path = $"{parent}/{child}";
            _paths[(parent, child)] = path;
        }

        return path;
    }

    /// <summary>
    /// The text of a tooltip begun, wrapped to a readable width, and the tooltip ended.
    /// </summary>
    private static void WriteTooltip(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static bool IsInside(Vector2 point, Vector2 min, Vector2 max)
    {
        return point.X >= min.X && point.Y >= min.Y && point.X < max.X && point.Y < max.Y;
    }

    /// <summary>
    /// The gem ImGui's clipboard callbacks are for: the one the platform's user data names.
    /// </summary>
    private static unsafe ImGuiDebugUIGem? Self()
    {
        void* user = ImGui.GetPlatformIO().PlatformClipboardUserData;
        return user == null ? null : GCHandle.FromIntPtr((nint)user).Target as ImGuiDebugUIGem;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void SetClipboard(ImGuiContext* context, byte* text)
    {
        if (Self() is { } gem)
        {
            gem._window.Clipboard = Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte* GetClipboard(ImGuiContext* context)
    {
        if (Self() is not { } gem)
        {
            return null;
        }

        if (gem._pasted.IsAllocated)
        {
            gem._pasted.Free();
        }

        byte[] bytes = Encoding.UTF8.GetBytes(gem._window.Clipboard + '\0');
        gem._pasted = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return (byte*)gem._pasted.AddrOfPinnedObject();
    }

    /// <summary>
    /// The window, or a view nested in the one open: a window gets a close button writing to <paramref name="visible"/>
    /// unless it is null.
    /// </summary>
    private unsafe void Begin(string title, bool* visible, bool scrollable)
    {
        ThrowIfOutsideFrame();
        int depth = _depth++;
        if (_levels.Count <= depth)
        {
            _levels.Add(new Level());
        }

        Level level = _levels[depth];
        level.Closed.Clear();
        level.IsScrollable = scrollable;
        level.IsNested = depth > 0;
        if (!level.IsNested)
        {
            level.Id = title;
            _windowsDrawn++;

            // Each new window a step down and right of the last, so none opens on top of another. Sizes go by the font,
            // which says how big the UI is.
            float step = _settings.FontSize * 2;
            if (_placed.Add(title))
            {
                ImGui.SetNextWindowPos(new Vector2(step * _placed.Count, step * _placed.Count), ImGuiCond.FirstUseEver);
            }

            if (scrollable)
            {
                ImGui.SetNextWindowSize(new Vector2(32, 18) * _settings.FontSize, ImGuiCond.FirstUseEver);
            }

            ImGui.Begin(title, visible, scrollable ? ImGuiWindowFlags.None : ImGuiWindowFlags.AlwaysAutoResize);
            return;
        }

        // As tall as the parent's room less what followed this view last frame: all of it, the first time.
        level.Id = PathOf(_levels[depth - 1].Id, title);
        float reserved = _reserved.GetValueOrDefault(level.Id);
        ImGuiWindowFlags flags = scrollable ? ImGuiWindowFlags.HorizontalScrollbar : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.BeginChild(title, new Vector2(0, -reserved), ImGuiChildFlags.Borders, flags);
    }

    /// <summary>
    /// Widgets go into ImGui's frame, which is open from this gem's Update to its LateUpdate; called outside it, ImGui
    /// would stop the process.
    /// </summary>
    private void ThrowIfOutsideFrame()
    {
        if (!_isInFrame)
        {
            throw new InvalidOperationException("The debug UI's widgets are drawn in Update, by a gem made after the UI gem.");
        }
    }

    /// <summary>
    /// Text in the world, at each place each window camera shows it within the settings' distance, on its plate, with
    /// its tooltip while the mouse rests on the plate and on no window. ImGui draws its background under every window,
    /// so the windows stay readable over what is reported.
    /// </summary>
    private void DrawWorld()
    {
        if (_world.Count == 0 || _views.Views.Count == 0)
        {
            return;
        }

        ImDrawListPtr under = ImGui.GetBackgroundDrawList();
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool canHover = !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        foreach (ShownReport report in _world)
        {
            foreach (CameraView view in _views.Views)
            {
                if (!view.Project(report.Position, _settings.TextDistance, out Vector2 center))
                {
                    continue;
                }

                Vector2 size = ImGui.CalcTextSize(report.Label);
                Vector2 corner = center - (size * 0.5f);
                Vector2 min = corner - _platePadding;
                Vector2 max = corner + size + _platePadding;
                under.AddRectFilled(min, max, Plate, 3f);
                under.AddText(corner, Packed(report.Color), report.Label);
                if (report.Tooltip is { } tooltip && canHover && IsInside(mouse, min, max))
                {
                    ImGui.BeginTooltip();
                    WriteTooltip(tooltip);
                }
            }
        }
    }

    /// <summary>
    /// The lines down the left of the window, oldest first, as many of the last as fit, with a line's tooltip while the
    /// mouse rests on it and on no window.
    /// </summary>
    private void DrawLines()
    {
        if (_lines.Count == 0)
        {
            return;
        }

        ImDrawListPtr under = ImGui.GetBackgroundDrawList();
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool canHover = !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        int fitting = Math.Max(1, (int)((_window.Height - (2 * _linesMargin.Y)) / lineHeight));
        Vector2 at = _linesMargin;
        for (int i = Math.Max(0, _lines.Count - fitting); i < _lines.Count; i++)
        {
            ShownReport line = _lines[i];
            under.AddText(at + Vector2.One, Shadow, line.Label);
            under.AddText(at, Packed(line.Color), line.Label);
            if (line.Tooltip is { } tooltip && canHover && IsInside(mouse, at, at + ImGui.CalcTextSize(line.Label)))
            {
                ImGui.BeginTooltip();
                WriteTooltip(tooltip);
            }

            at.Y += lineHeight;
        }
    }

    /// <summary>
    /// Loads the font on a worker, then has the main thread make it ImGui's between two frames. Until then, or when it
    /// does not load, ImGui writes in its own font.
    /// </summary>
    private async Task LoadFontAsync(Assets assets, Threads threads, CancellationToken stopping)
    {
        Handle<Font> handle = _settings.Font == Handle<Font>.None ? assets.Default<Font>() : _settings.Font;
        Result<Font> font = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
        if (!font.Ok)
        {
            Debugging.Warn($"The debug UI writes in ImGui's own font: its font does not load: {font.Error}");
            return;
        }

        threads.Post(EngineThread.Main, () =>
        {
            if (!stopping.IsCancellationRequested)
            {
                _font = new FontSource(font.Value, _settings.FontSize);
            }
        });
    }

    /// <summary>
    /// One open <see cref="Begin(string, bool*, bool)"/>: a window, or a view nested in one, and where the views nested
    /// in it closed.
    /// </summary>
    private sealed class Level
    {
        public string Id { get; set; } = string.Empty;

        public bool IsNested { get; set; }

        public bool IsScrollable { get; set; }

        public List<(string Id, float ClosedAt)> Closed { get; } = [];
    }

    /// <summary>
    /// A document's text as the NUL-terminated UTF-8 ImGui edits, with room to type into unless it is read-only.
    /// </summary>
    private sealed class DocumentBuffer
    {
        public DocumentBuffer(string text, bool readOnly)
        {
            Text = text;
            IsReadOnly = readOnly;
            Bytes = new byte[Encoding.UTF8.GetByteCount(text) + (readOnly ? 1 : 4096)];
            Encoding.UTF8.GetBytes(text, Bytes);
            Lines = 1 + text.AsSpan().Count('\n');
        }

        public string Text { get; }

        public bool IsReadOnly { get; }

        public byte[] Bytes { get; }

        public int Lines { get; }
    }
}

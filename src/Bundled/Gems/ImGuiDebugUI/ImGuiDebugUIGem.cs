using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using static Magic.ImGuiDebugUI.ImDrawFlags_;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImGuiBackendFlags_;
using static Magic.ImGuiDebugUI.ImGuiChildFlags_;
using static Magic.ImGuiDebugUI.ImGuiCol_;
using static Magic.ImGuiDebugUI.ImGuiComboFlags_;
using static Magic.ImGuiDebugUI.ImGuiCond_;
using static Magic.ImGuiDebugUI.ImGuiHoveredFlags_;
using static Magic.ImGuiDebugUI.ImGuiInputTextFlags_;
using static Magic.ImGuiDebugUI.ImGuiMouseButton_;
using static Magic.ImGuiDebugUI.ImGuiPopupFlags_;
using static Magic.ImGuiDebugUI.ImGuiSelectableFlags_;
using static Magic.ImGuiDebugUI.ImGuiSliderFlags_;
using static Magic.ImGuiDebugUI.ImGuiTabBarFlags_;
using static Magic.ImGuiDebugUI.ImGuiTabItemFlags_;
using static Magic.ImGuiDebugUI.ImGuiTableColumnFlags_;
using static Magic.ImGuiDebugUI.ImGuiTableFlags_;
using static Magic.ImGuiDebugUI.ImGuiTreeNodeFlags_;
using static Magic.ImGuiDebugUI.ImGuiWindowFlags_;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The debug UI, drawn with Dear ImGui: what gems and scripts report, as lines down the left of the window and text in
/// the world, and the windows gems draw with its widgets (<see cref="IDebugUI"/>).
/// <para>
/// ImGui's frame is the main thread's. <see cref="Update"/> hands ImGui the frame's input and starts its frame, so the
/// widgets gems made after this one call in their Update go into it; <see cref="LateUpdate"/> draws what is reported;
/// <see cref="Sync"/>, while both threads hold still, ends the frame and copies what ImGui drew
/// (<see cref="DrawLists"/>); the renderer draws that copy over the window with the gem's overlay script
/// (<see cref="Resources.Scripts.DebugUi"/>), once every view in the window is shown.
/// Reports come from any thread and are kept until they have shown (<see cref="Reports"/>).
/// </para>
/// </summary>
internal sealed class ImGuiDebugUIGem : IGem, IDebugUI, IDisposable
{
    private const int InputBytes = 256;

    /// <summary>
    /// ImGui's -FLT_MIN width: up to the right edge of whatever the field is in.
    /// </summary>
    private const float Stretch = -1.17549435E-38f;

    /// <summary>
    /// Text in the world, and the lines on screen, sit on a dark plate, so they read over anything behind them, a
    /// white debug view too.
    /// </summary>
    private const uint Plate = 0xC0000000;

    /// <summary>
    /// Whether what is reported shows whatever windows are open, as in a Debug build, or only while one is.
    /// </summary>
#if DEBUG
    private const bool IsAlwaysShowing = true;
#else
    private const bool IsAlwaysShowing = false;
#endif

    private static readonly Vector2 _platePadding = new(4, 2);
    private static readonly Vector2 _linesMargin = new(8, 8);

    // What reports are written in, linear as Color is: white, an orange warning and a red error.
    private static readonly Color _white = new(1, 1, 1);
    private static readonly Color _warning = new(1, 0.376f, 0);
    private static readonly Color _error = new(1, 0, 0);

    private readonly ImGuiDebugUISettings _settings;
    private readonly Assets _assets;
    private readonly IWindow _window;
    private readonly CancellationToken _stopping;
    private readonly IInput? _input;
    private readonly unsafe ImGuiContext_t* _context;
    private readonly Reports _reports = new();

    /// <summary>
    /// What ImGui is told this frame, as the C strings it reads: labels, the text of fields and lines.
    /// </summary>
    private readonly TextArena _text = new();
    private readonly CameraViews _views;
    private readonly Subscription _viewing;
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
    /// By a nested view's ImGui id, the height of what followed it in its parent last frame.
    /// </summary>
    private readonly Dictionary<uint, float> _reserved = [];

    /// <summary>
    /// By a document's id, its text as ImGui edits it, encoded once.
    /// </summary>
    private readonly Dictionary<string, DocumentBuffer> _documents = [];

    /// <summary>
    /// The windows given their first place, each a step down and right of the last.
    /// </summary>
    private readonly HashSet<string> _placed = [];

    /// <summary>
    /// By a row of tabs' ImGui id, the tab ImGui showed last frame.
    /// </summary>
    private readonly Dictionary<uint, int> _tabs = [];

    /// <summary>
    /// By a field's label, what it shows and its hidden ImGui id, made once rather than every frame.
    /// </summary>
    private readonly Dictionary<string, (string Shown, string Id)> _fields = [];

    /// <summary>
    /// This gem, for ImGui's clipboard callbacks.
    /// </summary>
    private readonly GCHandle _self;

    /// <summary>
    /// The clipboard's text as the NUL-terminated UTF-8 ImGui reads, pinned until it asks again.
    /// </summary>
    private GCHandle _pasted;

    /// <summary>
    /// The font the settings last named, so a change while running loads the new one.
    /// </summary>
    private Handle<Font> _fontAsked;

    private FontSource? _font;

    /// <summary>
    /// A font loaded for the settings, for the main thread to take in as Update starts; null for none.
    /// </summary>
    private Font? _arrivedFont;
    private int _depth;
    private int _windowsDrawn;
    private bool _isInFrame;
    private bool _hasLoggedUnended;
    private volatile bool _isShowing;

    /// <summary>
    /// Whether ImGui shows the table open, and how many columns it has; rows go nowhere while it does not.
    /// </summary>
    private bool _isTableShown;
    private int _tableColumns;

    public unsafe ImGuiDebugUIGem(
        ImGuiDebugUISettings settings,
        Assets assets,
        IWindow window,
        App app,
        World world,
        IInput? input = null)
    {
        _settings = settings;
        _window = window;
        _input = input;
        _assets = assets;
        _stopping = app.Quitting;
        _fontAsked = settings.Font;

        // ImGui writes no ini file, and is told the renderer takes the textures it makes and changes, and draws by
        // vertex offset; it takes the engine's look, at the settings' size.
        _context = ImGui_CreateContext(null);
        ImGui_SetCurrentContext(_context);
        ImGuiIO_t* io = ImGui_GetIO();
        io->IniFilename = null;
        io->BackendFlags |= (int)(ImGuiBackendFlags_RendererHasTextures | ImGuiBackendFlags_RendererHasVtxOffset);
        io->DisplayFramebufferScale = Vector2.One.Im;
        ImGuiStyle_t* style = ImGui_GetStyle();
        Look.Apply(style);
        style->FontSizeBase = settings.FontSize;

        // ImGui's clipboard is the window's, through callbacks that find this gem by the handle in the user data.
        _self = GCHandle.Alloc(this);
        ImGuiPlatformIO_t* platform = ImGui_GetPlatformIO();
        platform->Platform_ClipboardUserData = (void*)GCHandle.ToIntPtr(_self);
        platform->Platform_SetClipboardTextFn = &SetClipboard;
        platform->Platform_GetClipboardTextFn = &GetClipboard;

        _views = new CameraViews(window);
        _viewing = world.Register(_views);
        _isShowing = IsAlwaysShowing && window.Width > 0;
        LoadFontAsync().FireAndForget();
    }

    public bool Enabled => _isShowing;

    /// <summary>
    /// What ImGui drew last frame, copied in <see cref="Sync"/>, which the gem's overlay script draws from on the
    /// render thread.
    /// </summary>
    internal DrawLists DrawLists { get; } = new();

    public unsafe void Dispose()
    {
        _viewing.Dispose();
        ImGui_DestroyContext(_context);
        _font?.Dispose();
        _text.Dispose();
        _self.Free();
        if (_pasted.IsAllocated) _pasted.Free();
    }

    /// <summary>
    /// Hands ImGui the frame's input and starts its frame, which the widgets called in Update go into.
    /// </summary>
    public unsafe void Update(in Frame frame)
    {
        if (Interlocked.Exchange(ref _arrivedFont, null) is { } arrived)
        {
            _font?.Remove();
            _font?.Dispose();
            _font = new FontSource(arrived, _settings.FontSize);
        }

        if (_settings.Font != _fontAsked)
        {
            _fontAsked = _settings.Font;
            LoadFontAsync().FireAndForget();
        }

        _text.Reset();
        ImGuiIO_t* io = ImGui_GetIO();
        if (_input is { } input) ImGuiInput.Feed(io, input, _text);

        // Laid out in window pixels: the font size says how big the UI is. ImGui needs a time step of more than 0,
        // which a frame's may not be.
        io->DisplaySize = new Vector2(_window.Width, _window.Height).Im;
        io->DeltaTime = Math.Max(frame.RealDeltaSeconds, 1e-4f);
        ImGui_GetStyle()->FontSizeBase = _settings.FontSize;
        ImGui_NewFrame();
        _isInFrame = true;
        _windowsDrawn = 0;
    }

    /// <summary>
    /// Draws what is reported under the frame's windows: the last of the frame's widgets.
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

        // What is reported shows while a debug window is drawn, or always in a Debug build, and never with no window to
        // see it in; it is taken either way, so what has had its time goes.
        _isShowing = _window.Width > 0 && (IsAlwaysShowing || _windowsDrawn > 0);
        _world.Clear();
        _lines.Clear();
        _reports.Take(GetNow(), _world, _lines);

        if (!_isShowing) return;

        DrawWorld();
        DrawLines();
    }

    /// <summary>
    /// Ends ImGui's frame and copies what it drew for the render thread, which read the copy before until now: small
    /// enough to do while both threads hold still, rather than keep two copies.
    /// </summary>
    public unsafe void Sync(in Frame frame)
    {
        ImGui_Render();
        _isInFrame = false;
        DrawLists.Copy(ImGui_GetDrawData(), _window.Width, _window.Height);
    }

    public void Text(Vector3 position, string text, Color? color = null, string? tooltip = null)
        => _reports.Add(text, ReportKind.Plain, color ?? _white, position, tooltip, 0, GetNow());

    public void Text(string text, Color? color = null)
        => _reports.Add(text, ReportKind.Plain, color ?? _white, null, null, 0, GetNow());

    public void Info(
        string text,
        float? seconds = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        _reports.Add(text, ReportKind.Counted, _white, null, null, seconds ?? _settings.ReportSeconds, GetNow());
        Debugging.Info(text, member, file, line);
    }

    public void Warn(
        string text,
        Vector3? position = null,
        float? seconds = null,
        string? tooltip = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!_reports.Add(
            text,
            ReportKind.Alarm,
            _warning,
            position,
            tooltip,
            seconds ?? _settings.ReportSeconds,
            GetNow()))
        {
            return;
        }

        Debugging.Warn(text, member, file, line);
    }

    public void Error(
        string text,
        Vector3? position = null,
        float? seconds = null,
        string? tooltip = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!_reports.Add(
            text,
            ReportKind.Alarm,
            _error,
            position,
            tooltip,
            seconds ?? _settings.ReportSeconds,
            GetNow()))
        {
            return;
        }

        Debugging.Error(text, member, file, line);
    }

    public unsafe void Begin(string title, bool scrollable = false) => Begin(title, null, scrollable);

    public unsafe void Begin(string title, ref bool visible, bool scrollable = false)
    {
        fixed (bool* closable = &visible)
        {
            Begin(title, closable, scrollable);
        }
    }

    public void End()
    {
        if (_depth == 0) return;

        Level level = _levels[--_depth];

        // What followed each view nested in this one, measured now that everything after them is laid out.
        float end = ImGui_GetCursorPosY();
        foreach ((uint id, float closedAt) in level.Closed)
        {
            _reserved[id] = end - closedAt;
        }

        if (_depth == 0)
        {
            ImGui_End();

            return;
        }

        // Scrolled to the bottom: it stays there as what is in it grows.
        if (level.IsScrollable && ImGui_GetScrollY() >= ImGui_GetScrollMaxY() - 1f) ImGui_SetScrollHereY(1f);

        ImGui_EndChild();
        _levels[_depth - 1].Closed.Add((level.Id, ImGui_GetCursorPosY()));
    }

    public unsafe void Label(string text, Color? color = null)
    {
        ThrowIfOutsideFrame();
        if (color is not { } tint)
        {
            ImGui_TextUnformatted(_text.Of(text));

            return;
        }

        ImGui_PushStyleColor((int)ImGuiCol_Text, PackColor(tint));
        ImGui_TextUnformatted(_text.Of(text));
        ImGui_PopStyleColor();
    }

    public unsafe bool Button(string label)
    {
        ThrowIfOutsideFrame();

        return ImGui_Button(_text.Of(label));
    }

    public unsafe bool Input(string label, ref string text, string? hint = null, bool focus = false)
    {
        ThrowIfOutsideFrame();
        if (focus && ImGui_IsWindowAppearing()) ImGui_SetKeyboardFocusHere();

        // Text too long for the field is cut, not an error.
        Utf8.FromUtf16(text, _typed.AsSpan(0, InputBytes - 1), out _, out int written);
        _typed[written] = 0;

        byte* id = LabelField(label);
        bool isEntered;
        fixed (byte* buffer = _typed)
        {
            isEntered = hint is null
                ? ImGui_InputText(id, buffer, InputBytes, (int)ImGuiInputTextFlags_EnterReturnsTrue)
                : ImGui_InputTextWithHint(
                    id,
                    _text.Of(hint),
                    buffer,
                    InputBytes,
                    (int)ImGuiInputTextFlags_EnterReturnsTrue);
        }

        if (isEntered || ImGui_IsItemEdited())
            text = Encoding.UTF8.GetString(_typed, 0, Array.IndexOf(_typed, (byte)0));

        if (isEntered)
        {
            // The field keeps the keyboard, to type the next line.
            ImGui_SetKeyboardFocusHereEx(-1);
        }

        return isEntered;
    }

    public unsafe bool Document(string label, ref string text, bool readOnly = false)
    {
        ThrowIfOutsideFrame();
        byte* id = LabelField(label);

        // Encoded again only for another string than the one handed back last, or a change to read-only.
        if (!_documents.TryGetValue(label, out DocumentBuffer? document) || !ReferenceEquals(document.Text, text)
            || document.IsReadOnly != readOnly)
        {
            document = new DocumentBuffer(text, readOnly);
            _documents[label] = document;
        }

        float height = (document.Lines * ImGui_GetTextLineHeight()) + (ImGui_GetStyle()->FramePadding.y * 2);
        ImGuiInputTextFlags_ flags = readOnly ? ImGuiInputTextFlags_ReadOnly : ImGuiInputTextFlags_None;
        bool isChanged;
        fixed (byte* buffer = document.Bytes)
        {
            isChanged = ImGui_InputTextMultilineEx(
                id,
                buffer,
                (nuint)document.Bytes.Length,
                new Vector2(Stretch, height).Im,
                (int)flags,
                null,
                null);
        }

        if (!isChanged) return false;

        text = Encoding.UTF8.GetString(document.Bytes, 0, Array.IndexOf(document.Bytes, (byte)0));
        _documents[label] = new DocumentBuffer(text, readOnly);

        return true;
    }

    public unsafe bool Checkbox(string label, ref bool value)
    {
        ThrowIfOutsideFrame();
        fixed (bool* flag = &value)
        {
            return ImGui_Checkbox(LabelField(label), flag);
        }
    }

    public unsafe bool Slider(string label, ref float value, float min = 0f, float max = 0f)
    {
        ThrowIfOutsideFrame();

        // A drag moves the value a hundredth of itself a pixel, and at least a tenth.
        float speed = Math.Max(Math.Abs(value) * 0.01f, 0.1f);
        fixed (float* held = &value)
        {
            return ImGui_DragFloatEx(
                LabelField(label),
                held,
                speed,
                min,
                max,
                Literal("%.6g"u8),
                (int)ImGuiSliderFlags_None);
        }
    }

    public unsafe bool Choice(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        byte* id = LabelField(label);

        if (!ImGui_BeginCombo(
            id,
            _text.Of(index >= 0 && index < options.Length ? options[index] : string.Empty),
            (int)ImGuiComboFlags_None))
        {
            return false;
        }

        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            if (ImGui_SelectableEx(_text.Of(options[i]), i == index, (int)ImGuiSelectableFlags_None, default))
            {
                chosen = i;
            }
        }

        ImGui_EndCombo();

        if (chosen == index) return false;

        index = chosen;

        return true;
    }

    public unsafe bool Tabs(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        byte* title = _text.Of(label);
        uint id = ImGui_GetID(title);

        if (!ImGui_BeginTabBar(title, (int)ImGuiTabBarFlags_None)) return false;

        // ImGui keeps its own selection: the caller's index is pushed only when it is not the tab ImGui last showed.
        int shown = _tabs.GetValueOrDefault(id, -1);
        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            ImGuiTabItemFlags_ flags = i == index && index != shown
                ? ImGuiTabItemFlags_SetSelected
                : ImGuiTabItemFlags_None;

            if (!ImGui_BeginTabItem(_text.Of(options[i]), null, (int)flags)) continue;

            chosen = i;
            ImGui_EndTabItem();
        }

        ImGui_EndTabBar();
        _tabs[id] = chosen;

        if (chosen == index) return false;

        index = chosen;

        return true;
    }

    public unsafe bool Header(string label, bool folds = false)
    {
        ThrowIfOutsideFrame();
        if (folds) ImGui_SetNextItemOpen(false, (int)ImGuiCond_Always);

        return ImGui_CollapsingHeader(_text.Of(label), (int)ImGuiTreeNodeFlags_DefaultOpen);
    }

    public unsafe void Table(string label, ReadOnlySpan<string> columns)
    {
        ThrowIfOutsideFrame();
        _tableColumns = columns.Length;
        _isTableShown = ImGui_BeginTable(
            _text.Of(label),
            columns.Length,
            (int)(ImGuiTableFlags_Resizable | ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_RowBg));

        if (!_isTableShown) return;

        foreach (string column in columns)
        {
            ImGui_TableSetupColumn(_text.Of(column), (int)ImGuiTableColumnFlags_None);
        }

        ImGui_TableHeadersRow();
    }

    public unsafe void Row(ReadOnlySpan<string> cells)
    {
        ThrowIfOutsideFrame();

        if (!_isTableShown) return;

        ImGui_TableNextRow();
        for (int i = 1; i < cells.Length && i < _tableColumns; i++)
        {
            ImGui_TableSetColumnIndex(i);
            ImGui_TextUnformatted(_text.Of(cells[i]));
        }

        // The first cell last, spanning the row, so the row is the item a tooltip after it rests on. Rows may start
        // with the same text, so the row's index, not its text, is its id.
        if (cells.Length == 0 || _tableColumns <= 0) return;

        ImGui_TableSetColumnIndex(0);
        ImGui_PushIDInt(ImGui_TableGetRowIndex());
        ImGui_SelectableEx(
            _text.Of(cells[0]),
            false,
            (int)(ImGuiSelectableFlags_SpanAllColumns | ImGuiSelectableFlags_Disabled),
            default);
        ImGui_PopID();
    }

    public void EndTable()
    {
        ThrowIfOutsideFrame();
        if (_isTableShown) ImGui_EndTable();

        _isTableShown = false;
    }

    public void Tooltip(string text)
    {
        ThrowIfOutsideFrame();
        if (ImGui_BeginItemTooltip()) WriteTooltip(text);
    }

    public unsafe bool MenuItem(string label)
    {
        ThrowIfOutsideFrame();

        // Over the window's own items and nested views too (a document is one), not only its empty space.
        const ImGuiHoveredFlags_ Over = ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_AllowWhenBlockedByActiveItem
            | ImGuiHoveredFlags_AllowWhenBlockedByPopup;
        byte* menu = Literal("##context"u8);
        if (ImGui_IsMouseReleased((int)ImGuiMouseButton_Right) && ImGui_IsWindowHovered((int)Over))
            ImGui_OpenPopup(menu, (int)ImGuiPopupFlags_None);

        if (!ImGui_BeginPopup(menu, (int)ImGuiWindowFlags_None)) return false;

        bool isClicked = ImGui_MenuItem(_text.Of(label));
        ImGui_EndPopup();

        return isClicked;
    }

    /// <summary>
    /// The time reports are kept by, in seconds.
    /// </summary>
    private static double GetNow() => Environment.TickCount64 / 1000d;

    /// <summary>
    /// A linear colour as ImGui packs one, encoded to sRGB as the window's image wants: alpha, blue, green, red from
    /// the top byte down.
    /// </summary>
    private static uint PackColor(Color color)
    {
        return (Byte(color.A) << 24) | (Byte(Encode(color.B)) << 16) | (Byte(Encode(color.G)) << 8)
            | Byte(Encode(color.R));

        static float Encode(float linear)
            => linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;

        static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>
    /// A UTF-8 literal as the C string ImGui reads: the literal's bytes sit in the assembly's static data and never
    /// move, so no copy and no pinning is needed. For <c>u8</c> literals only.
    /// </summary>
    private static unsafe byte* Literal(ReadOnlySpan<byte> utf8)
        => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(utf8));

    /// <summary>
    /// The gem ImGui's clipboard callbacks are for: the one the platform's user data names, set as the context is made.
    /// </summary>
    private static unsafe ImGuiDebugUIGem? GetSelf()
        => GCHandle.FromIntPtr((nint)ImGui_GetPlatformIO()->Platform_ClipboardUserData).Target as ImGuiDebugUIGem;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void SetClipboard(ImGuiContext_t* context, byte* text)
    {
        if (GetSelf() is { } gem) gem._window.Clipboard = CString.Read(text);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte* GetClipboard(ImGuiContext_t* context)
    {
        if (GetSelf() is not { } gem) return null;

        if (gem._pasted.IsAllocated) gem._pasted.Free();

        byte[] bytes = Encoding.UTF8.GetBytes(gem._window.Clipboard + '\0');
        gem._pasted = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        return (byte*)gem._pasted.AddrOfPinnedObject();
    }

    /// <summary>
    /// Lays out a field: its label, up to any <c>##</c>, on the left, then the field stretched to the right edge.
    /// Returns the field's hidden ImGui id: the whole label, so fields that share what they show stay apart.
    /// </summary>
    private unsafe byte* LabelField(string label)
    {
        if (!_fields.TryGetValue(label, out (string Shown, string Id) field))
        {
            int hidden = label.IndexOf("##", StringComparison.Ordinal);
            field = (hidden < 0 ? label : label[..hidden], "##" + label);
            _fields[label] = field;
        }

        if (field.Shown.Length > 0)
        {
            ImGui_AlignTextToFramePadding();
            ImGui_TextUnformatted(_text.Of(field.Shown));
            ImGui_SameLine();
        }

        ImGui_SetNextItemWidth(Stretch);

        return _text.Of(field.Id);
    }

    /// <summary>
    /// The text of a tooltip begun, wrapped to a readable width, and the tooltip ended.
    /// </summary>
    private unsafe void WriteTooltip(string text)
    {
        ImGui_PushTextWrapPos(ImGui_GetFontSize() * 30f);
        ImGui_TextUnformatted(_text.Of(text));
        ImGui_PopTextWrapPos();
        ImGui_EndTooltip();
    }

    /// <summary>
    /// The window, or a view nested in the one open: a window gets a close button writing to <paramref name="visible"/>
    /// unless it is null.
    /// </summary>
    private unsafe void Begin(string title, bool* visible, bool scrollable)
    {
        ThrowIfOutsideFrame();
        int depth = _depth++;
        if (_levels.Count <= depth) _levels.Add(new Level());

        Level level = _levels[depth];
        level.Closed.Clear();
        level.IsScrollable = scrollable;
        if (depth == 0)
        {
            _windowsDrawn++;

            // Each new window a step down and right of the last, so none opens on top of another. Sizes go by the font,
            // which says how big the UI is.
            float step = _settings.FontSize * 2;
            if (_placed.Add(title))
            {
                ImGui_SetNextWindowPos(
                    new Vector2(step * _placed.Count, step * _placed.Count).Im,
                    (int)ImGuiCond_FirstUseEver);
            }

            if (scrollable)
                ImGui_SetNextWindowSize((new Vector2(32, 18) * _settings.FontSize).Im, (int)ImGuiCond_FirstUseEver);

            ImGui_Begin(
                _text.Of(title),
                visible,
                scrollable ? (int)ImGuiWindowFlags_None : (int)ImGuiWindowFlags_AlwaysAutoResize);

            return;
        }

        // As tall as the parent's room less what followed this view last frame: all of it, the first time. The view's
        // id is the one ImGui gives it, from the title and the window it is in.
        byte* name = _text.Of(title);
        level.Id = ImGui_GetID(name);
        float reserved = _reserved.GetValueOrDefault(level.Id);
        ImGuiWindowFlags_ flags = scrollable
            ? ImGuiWindowFlags_HorizontalScrollbar
            : ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse;
        ImGui_BeginChild(name, new Vector2(0, -reserved).Im, (int)ImGuiChildFlags_Borders, (int)flags);
    }

    /// <summary>
    /// Widgets go into ImGui's frame, which is open from this gem's Update to its LateUpdate; called outside it, ImGui
    /// would stop the process.
    /// </summary>
    private void ThrowIfOutsideFrame()
    {
        if (_isInFrame) return;

        throw new InvalidOperationException(
            "The debug UI's widgets are drawn in Update, by a gem made after the UI gem.");
    }

    /// <summary>
    /// Text in the world, at each place each window camera shows it within the settings' distance, on its plate, with
    /// its tooltip while the mouse rests on the plate and on no window. ImGui draws its background under every window,
    /// so the windows stay readable over what is reported.
    /// </summary>
    private unsafe void DrawWorld()
    {
        if (_world.Count == 0 || _views.Views.Count == 0) return;

        ImDrawList_t* under = ImGui_GetBackgroundDrawList();
        bool canHover = !ImGui_IsWindowHovered((int)ImGuiHoveredFlags_AnyWindow);
        foreach (ShownReport report in _world)
        {
            foreach (CameraView view in _views.Views)
            {
                if (!view.TryProject(report.Position, _settings.TextDistance, out Vector2 center)) continue;

                byte* label = _text.Of(report.Label);
                Vector2 size = ImGui_CalcTextSize(label).Vector;
                Vector2 corner = center - (size * 0.5f);
                Vector2 min = corner - _platePadding;
                Vector2 max = corner + size + _platePadding;
                ImDrawList_AddRectFilledEx(under, min.Im, max.Im, Plate, 3f, (int)ImDrawFlags_None);
                ImDrawList_AddText(under, corner.Im, PackColor(report.Color), label);
                if (report.Tooltip is { } tooltip && canHover && ImGui_IsMouseHoveringRect(min.Im, max.Im))
                {
                    ImGui_BeginTooltip();
                    WriteTooltip(tooltip);
                }
            }
        }
    }

    /// <summary>
    /// The lines down the left of the window, oldest first, as many of the last as fit, with a line's tooltip while the
    /// mouse rests on it and on no window.
    /// </summary>
    private unsafe void DrawLines()
    {
        if (_lines.Count == 0) return;

        ImDrawList_t* under = ImGui_GetBackgroundDrawList();
        bool canHover = !ImGui_IsWindowHovered((int)ImGuiHoveredFlags_AnyWindow);
        float lineHeight = ImGui_GetTextLineHeightWithSpacing();
        int fitting = Math.Max(1, (int)((_window.Height - (2 * _linesMargin.Y)) / lineHeight));
        Vector2 at = _linesMargin;
        for (int i = Math.Max(0, _lines.Count - fitting); i < _lines.Count; i++)
        {
            ShownReport line = _lines[i];
            byte* label = _text.Of(line.Label);
            Vector2 size = ImGui_CalcTextSize(label).Vector;
            ImDrawList_AddRectFilledEx(
                under,
                (at - _platePadding).Im,
                (at + size + _platePadding).Im,
                Plate,
                3f,
                (int)ImDrawFlags_None);
            ImDrawList_AddText(under, at.Im, PackColor(line.Color), label);
            if (line.Tooltip is { } tooltip && canHover && ImGui_IsMouseHoveringRect(at.Im, (at + size).Im))
            {
                ImGui_BeginTooltip();
                WriteTooltip(tooltip);
            }

            at.Y += lineHeight;
        }
    }

    /// <summary>
    /// Loads the settings' font on a worker, then has the main thread make it ImGui's between two frames, in place of
    /// the one before. Until the first loads, or when it does not load, ImGui writes in its own font, or in the one
    /// before.
    /// </summary>
    private async Task LoadFontAsync()
    {
        Handle<Font> handle = _settings.Font == Handle<Font>.None ? _assets.Default<Font>() : _settings.Font;
        Result<Font> font = await _assets.LoadAsync(handle, _stopping).ConfigureAwait(false);
        if (!font.Ok)
        {
            Debugging.Warn($"The debug UI writes in the font it did: its font does not load: {font.Error}");

            return;
        }

        Volatile.Write(ref _arrivedFont, font.Value);
    }

    /// <summary>
    /// One open <see cref="Begin(string, bool*, bool)"/>: a window, or a view nested in one, and where the views nested
    /// in it closed.
    /// </summary>
    private sealed class Level
    {
        /// <summary>
        /// A nested view's ImGui id; a window needs none.
        /// </summary>
        internal uint Id { get; set; }

        internal bool IsScrollable { get; set; }

        internal List<(uint Id, float ClosedAt)> Closed { get; } = [];
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

        internal string Text { get; }

        internal bool IsReadOnly { get; }

        internal byte[] Bytes { get; }

        internal int Lines { get; }
    }
}

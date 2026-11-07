using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Input;
using Magic.Platform;
using Magic.Projects;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;
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
using static Magic.ImGuiDebugUI.ImTextureStatus;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The debug UI, drawn with Dear ImGui: what gems and scripts report, and the lines they log to show on screen, as lines
/// down the left of the picture and text in the world, and the windows gems draw with its widgets (<see cref="IDebugUI"/>).
/// <para>
/// ImGui's frame is the game thread's. <see cref="Update"/> hands ImGui the frame's input and starts its frame, so the
/// widgets gems made after this one call in their Update go into it; <see cref="LateUpdate"/> draws what is reported, ends
/// the frame and publishes a copy of it, whether anything draws it or not; and <see cref="Render"/> records the newest
/// copy over the window's image, in the overlay layer, so it shows over whatever drew the world. Reports come from any thread (<see cref="Reports"/>). It is public because
/// nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </para>
/// </summary>
public sealed class ImGuiDebugUIGem : IGem, IDebugUI, ILogSink, IDisposable
{
    private const int InputBytes = 256;

    /// <summary>
    /// The height of a line of text in target pixels, which every widget is sized around.
    /// </summary>
    private const float FontSize = 20;

    /// <summary>
    /// How wide a window that fits what is in it is at least, in font sizes.
    /// </summary>
    private const float FittedWindowWidth = 16;

    /// <summary>
    /// How big a window the user sizes opens, in font sizes.
    /// </summary>
    private const float ScrollableWindowWidth = 32;

    private const float ScrollableWindowHeight = 18;

    /// <summary>
    /// Seconds a line logged to show on screen stays.
    /// </summary>
    private const float ReportSec = 5;

    /// <summary>
    /// Metres from the camera beyond which text in the world is not shown.
    /// </summary>
    private const float TextDistance = 50;

    /// <summary>
    /// ImGui's -FLT_MIN width: up to the right edge of whatever the field is in.
    /// </summary>
    private const float Stretch = -1.17549435E-38f;

    /// <summary>
    /// Text in the world, and the lines on screen, sit on a dark plate, so they read over anything behind them, a white
    /// debug view too.
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

    /// <summary>
    /// The size ImGui lays out in without a window, the one the renderer then draws.
    /// </summary>
    private static readonly Vector2 _windowlessSize = new(1280, 720);
    private static readonly Vector2 _platePadding = new(4, 2);
    private static readonly Vector2 _linesMargin = new(8, 8);

    // What reports are written in, linear as Color is: white, an orange warning and a red error.
    private static readonly Color _white = new(1, 1, 1);
    private static readonly Color _warning = new(1, 0.376f, 0);
    private static readonly Color _error = new(1, 0, 0);

    private readonly App _app;
    private readonly IEcs _ecs;
    private readonly IInput? _input;
    private readonly IClipboard? _clipboard;
    private readonly unsafe ImGuiContext_t* _context;
    private readonly DebugUIOverlay _overlay;
    private readonly Reports _reports = new();

    /// <summary>
    /// Each frame ImGui ends, copied for the overlay, and what ImGui asks of its textures, in the order it asks.
    /// </summary>
    private readonly Mailbox<DebugUIFrame> _frames = new();
    private readonly Channel<DebugUITextureChange> _textureChanges = Channel.CreateUnbounded<DebugUITextureChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// What ImGui is told this frame, as the C strings it reads: labels, the text of fields and lines.
    /// </summary>
    private readonly TextArena _text = new();
    private readonly List<ShownReport> _worldTexts = [];
    private readonly List<ShownReport> _screenLines = [];

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
    private readonly GCHandle<ImGuiDebugUIGem> _self;

    /// <summary>
    /// The id the last of ImGui's textures was given, which the overlay knows it by.
    /// </summary>
    private ulong _lastTextureId;

    /// <summary>
    /// Where the next new window's left edge goes.
    /// </summary>
    private float _nextWindowX = FontSize * 2;
    private int _depth;
    private bool _isWindowDrawn;
    private volatile bool _isShowing;

    /// <summary>
    /// Whether ImGui shows the table open; rows go nowhere while it does not.
    /// </summary>
    private bool _isTableShown;

    public unsafe ImGuiDebugUIGem(
        App app,
        AssetManager assets,
        IEcs ecs,
        IInput? input = null,
        IClipboard? clipboard = null
    )
    {
        _app = app;
        _ecs = ecs;
        _input = input;
        _clipboard = clipboard;
        _overlay = new DebugUIOverlay(assets, _frames, _textureChanges.Reader);

        // ImGui writes no ini file, and is told the overlay takes the textures it makes and changes, and draws by vertex
        // offset; it takes the engine's look, at its size.
        _context = ImGui_CreateContext(null);
        ImGui_SetCurrentContext(_context);
        ImGuiIO_t* io = ImGui_GetIO();
        io->IniFilename = null;
        io->BackendFlags |= (int)(ImGuiBackendFlags_RendererHasTextures | ImGuiBackendFlags_RendererHasVtxOffset);
        io->DisplayFramebufferScale = Vector2.One.Im;
        ImGuiStyle_t* style = ImGui_GetStyle();
        Look.Apply(style);
        style->FontSizeBase = FontSize;

        // ImGui's clipboard is the platform's, through callbacks that find this gem by the handle in the user data.
        _self = new GCHandle<ImGuiDebugUIGem>(this);
        ImGuiPlatformIO_t* platform = ImGui_GetPlatformIO();
        platform->Platform_ClipboardUserData = (void*)GCHandle<ImGuiDebugUIGem>.ToIntPtr(_self);
        platform->Platform_SetClipboardTextFn = &SetClipboard;
        platform->Platform_GetClipboardTextFn = &GetClipboard;
        _isShowing = IsAlwaysShowing;
    }

    public bool IsEnabled => _isShowing;

    public unsafe void Dispose()
    {
        ImGui_DestroyContext(_context);
        _text.Dispose();
        _self.Dispose();
    }

    /// <summary>
    /// Hands ImGui the frame's input and starts its frame, which the widgets called in Update go into.
    /// </summary>
    public unsafe void Update(in Frame frame)
    {
        _text.Reset();
        ImGuiIO_t* io = ImGui_GetIO();
        if (_input is { } input)
            ImGuiInput.Feed(io, input, _text);

        // ImGui needs a time step of more than 0, which a frame's may not be.
        io->DisplaySize = GetDisplaySize().Im;
        io->DeltaTime = Math.Max((float)frame.AppDeltaSec, 1e-4f);
        ImGui_NewFrame();
        _isWindowDrawn = false;
    }

    /// <summary>
    /// Draws what is reported under the frame's windows, the last of the frame's widgets, then ends ImGui's frame, which
    /// it needs before the next begins, and publishes a copy of it for the overlay.
    /// </summary>
    public unsafe void LateUpdate(in Frame frame)
    {
        if (_depth > 0)
            Text("A debug UI window was begun and not ended; it is ended for it.", _error);

        while (_depth > 0)
        {
            End();
        }

        // What is reported shows while a debug window is drawn, or always in a Debug build; it is taken either way, so
        // what has had its time goes.
        _isShowing = IsAlwaysShowing || _isWindowDrawn;
        _worldTexts.Clear();
        _screenLines.Clear();
        _reports.Take(frame.AppSec, _worldTexts, _screenLines);
        if (_isShowing)
            DrawReports();

        // Ended whether or not anything draws it. Its textures get their ids before the copy, which reads them.
        ImGui_Render();
        QueueTextureChanges(frame.Index);
        _frames.Back.Copy(ImGui_GetDrawData(), frame.Index);
        _frames.Publish();
    }

    /// <summary>
    /// The newest frame ImGui ended, over the window's image.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph) => _overlay.Record(graph);

    public void Text(Vector3 position, string text, Color? color = null, string? tooltip = null) =>
        _reports.Add(new Report(text, ReportKind.Plain, color ?? _white, position, tooltip, 0));

    public void Text(string text, Color? color = null) =>
        _reports.Add(new Report(text, ReportKind.Plain, color ?? _white, null, null, 0));

    /// <summary>
    /// Shows a line logged to show on screen for a while: information counted while it shows, and a warning or an error
    /// between <c>!!!</c>s. Any thread.
    /// </summary>
    public void Write(in Log.Line line)
    {
        if (!line.IsOnScreen)
            return;

        (ReportKind kind, Color color) = line.Level switch
        {
            Log.LoggingLevel.Verbose or Log.LoggingLevel.Info => (ReportKind.Counted, _white),
            Log.LoggingLevel.Warn => (ReportKind.Alarm, _warning),
            Log.LoggingLevel.Error or Log.LoggingLevel.Critical => (ReportKind.Alarm, _error),
            _ => (ReportKind.Counted, _white),
        };
        _reports.Add(new Report(line.Message, kind, color, null, null, ReportSec));
    }

    /// <summary>
    /// Nothing: a line shows with the next frame drawn.
    /// </summary>
    public void Flush() { }

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
        if (_depth == 0)
            return;

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
        if (level.IsScrollable && ImGui_GetScrollY() >= ImGui_GetScrollMaxY() - 1f)
            ImGui_SetScrollHereY(1f);

        ImGui_EndChild();
        _levels[_depth - 1].Closed.Add((level.Id, ImGui_GetCursorPosY()));
    }

    public unsafe void Label(string text, Color? color = null)
    {
        ThrowIfOutsideFrame();
        if (color is not { } tint)
        {
            ImGui_TextUnformatted(_text.Add(text));

            return;
        }

        ImGui_PushStyleColor((int)ImGuiCol_Text, PackColor(tint));
        ImGui_TextUnformatted(_text.Add(text));
        ImGui_PopStyleColor();
    }

    public unsafe bool Button(string label)
    {
        ThrowIfOutsideFrame();

        return ImGui_Button(_text.Add(label));
    }

    public unsafe bool Input(string label, ref string text, string? hint = null, bool focus = false)
    {
        ThrowIfOutsideFrame();
        if (focus && ImGui_IsWindowAppearing())
            ImGui_SetKeyboardFocusHere();

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
                    _text.Add(hint),
                    buffer,
                    InputBytes,
                    (int)ImGuiInputTextFlags_EnterReturnsTrue
                );
            if (isEntered || ImGui_IsItemEdited())
                text = CString.Read(buffer);
        }

        // The field keeps the keyboard, to type the next line.
        if (isEntered)
            ImGui_SetKeyboardFocusHereEx(-1);

        return isEntered;
    }

    public unsafe bool Document(string label, ref string text, bool readOnly = false)
    {
        ThrowIfOutsideFrame();
        byte* id = LabelField(label);

        // Encoded again only for another string than the one handed back last, or a change to read-only.
        if (
            !_documents.TryGetValue(label, out DocumentBuffer? document)
            || !ReferenceEquals(document.Text, text)
            || document.IsReadOnly != readOnly
        )
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
                null
            );
            if (isChanged)
                text = CString.Read(buffer);
        }

        return isChanged;
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
                (int)ImGuiSliderFlags_None
            );
        }
    }

    public unsafe bool Choice(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        byte* id = LabelField(label);
        if (
            !ImGui_BeginCombo(
                id,
                _text.Add(index >= 0 && index < options.Length ? options[index] : string.Empty),
                (int)ImGuiComboFlags_None
            )
        )
        {
            return false;
        }

        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            if (ImGui_SelectableEx(_text.Add(options[i]), i == index, (int)ImGuiSelectableFlags_None, default))
                chosen = i;
        }

        ImGui_EndCombo();
        if (chosen == index)
            return false;

        index = chosen;

        return true;
    }

    public unsafe bool Tabs(string label, ref int index, ReadOnlySpan<string> options)
    {
        ThrowIfOutsideFrame();
        byte* title = _text.Add(label);
        uint id = ImGui_GetID(title);
        if (!ImGui_BeginTabBar(title, (int)ImGuiTabBarFlags_None))
            return false;

        // ImGui keeps its own selection: the caller's index is pushed only when it is not the tab ImGui last showed.
        int shown = _tabs.GetValueOrDefault(id, -1);
        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            ImGuiTabItemFlags_ flags =
                i == index && index != shown ? ImGuiTabItemFlags_SetSelected : ImGuiTabItemFlags_None;
            if (!ImGui_BeginTabItem(_text.Add(options[i]), null, (int)flags))
                continue;

            chosen = i;
            ImGui_EndTabItem();
        }

        ImGui_EndTabBar();
        _tabs[id] = chosen;
        if (chosen == index)
            return false;

        index = chosen;

        return true;
    }

    public unsafe bool Header(string label, bool isOpenAtStart = true, int depth = 0)
    {
        ThrowIfOutsideFrame();

        // Indented a step a level from where the window or view's last header left it; its own Begin starts at none.
        if (_depth > 0 && _levels[_depth - 1] is { } level && level.Indent != depth)
        {
            float steps = (depth - level.Indent) * ImGui_GetStyle()->IndentSpacing;
            if (steps > 0)
                ImGui_IndentEx(steps);
            else
                ImGui_UnindentEx(-steps);

            level.Indent = depth;
        }

        return ImGui_CollapsingHeader(_text.Add(label), isOpenAtStart ? (int)ImGuiTreeNodeFlags_DefaultOpen : 0);
    }

    public unsafe void Table(string label, ReadOnlySpan<string> columns)
    {
        ThrowIfOutsideFrame();
        _isTableShown = ImGui_BeginTable(
            _text.Add(label),
            columns.Length,
            (int)(ImGuiTableFlags_Resizable | ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_RowBg)
        );
        if (!_isTableShown)
            return;

        foreach (string column in columns)
        {
            ImGui_TableSetupColumn(_text.Add(column), (int)ImGuiTableColumnFlags_None);
        }

        ImGui_TableHeadersRow();
    }

    public unsafe void Row(ReadOnlySpan<string> cells)
    {
        ThrowIfOutsideFrame();
        if (!_isTableShown)
            return;

        ImGui_TableNextRow();
        int columns = ImGui_TableGetColumnCount();
        for (int i = 1; i < cells.Length && i < columns; i++)
        {
            ImGui_TableSetColumnIndex(i);
            ImGui_TextUnformatted(_text.Add(cells[i]));
        }

        // The first cell last, spanning the row, so the row is the item a tooltip after it rests on. Rows may start with
        // the same text, so the row's index, not its text, is its id.
        if (cells.Length == 0)
            return;

        ImGui_TableSetColumnIndex(0);
        ImGui_PushIDInt(ImGui_TableGetRowIndex());
        ImGui_SelectableEx(_text.Add(cells[0]), false, (int)ImGuiSelectableFlags_SpanAllColumns, default);
        ImGui_PopID();
    }

    public void EndTable()
    {
        ThrowIfOutsideFrame();
        if (_isTableShown)
            ImGui_EndTable();

        _isTableShown = false;
    }

    public void Tooltip(string text)
    {
        ThrowIfOutsideFrame();
        if (ImGui_BeginItemTooltip())
            WriteTooltip(text);
    }

    public unsafe bool MenuItem(string label)
    {
        ThrowIfOutsideFrame();

        // Over the window's own items and nested views too (a document is one), not only its empty space.
        const ImGuiHoveredFlags_ Over =
            ImGuiHoveredFlags_ChildWindows
            | ImGuiHoveredFlags_AllowWhenBlockedByActiveItem
            | ImGuiHoveredFlags_AllowWhenBlockedByPopup;
        byte* menu = Literal("##context"u8);
        if (ImGui_IsMouseReleased((int)ImGuiMouseButton_Right) && ImGui_IsWindowHovered((int)Over))
            ImGui_OpenPopup(menu, (int)ImGuiPopupFlags_None);

        if (!ImGui_BeginPopup(menu, (int)ImGuiWindowFlags_None))
            return false;

        bool isClicked = ImGui_MenuItem(_text.Add(label));
        ImGui_EndPopup();

        return isClicked;
    }

    /// <summary>
    /// A linear colour as ImGui packs one, encoded to sRGB as the target wants: alpha, blue, green, red from the top byte
    /// down.
    /// </summary>
    private static uint PackColor(Color color)
    {
        return (Byte(color.A) << 24)
            | (Byte(Encode(color.B)) << 16)
            | (Byte(Encode(color.G)) << 8)
            | Byte(Encode(color.R));

        static float Encode(float linear) =>
            linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;

        static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>
    /// A UTF-8 literal as the C string ImGui reads: the literal's bytes sit in the assembly's static data and never move, so
    /// no copy and no pinning is needed. For <c>u8</c> literals only.
    /// </summary>
    private static unsafe byte* Literal(ReadOnlySpan<byte> utf8) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(utf8));

    /// <summary>
    /// The gem ImGui's clipboard callbacks are for: the one the platform's user data names, set as the context is made.
    /// </summary>
    private static unsafe ImGuiDebugUIGem GetSelf() =>
        GCHandle<ImGuiDebugUIGem>.FromIntPtr((nint)ImGui_GetPlatformIO()->Platform_ClipboardUserData).Target;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void SetClipboard(ImGuiContext_t* context, byte* text)
    {
        if (GetSelf()._clipboard is { } clipboard)
            clipboard.Text = CString.Read(text);
    }

    /// <summary>
    /// The clipboard's text from the frame's arena: ImGui copies it within the call, which a widget makes during the frame.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte* GetClipboard(ImGuiContext_t* context)
    {
        ImGuiDebugUIGem gem = GetSelf();

        return gem._clipboard is { } clipboard ? gem._text.Add(clipboard.Text) : null;
    }

    /// <summary>
    /// The window's size in pixels, which ImGui lays out in and the renderer draws the UI over.
    /// </summary>
    private Vector2 GetDisplaySize() =>
        _app.Window is { Width: > 0, Height: > 0 } window ? new Vector2(window.Width, window.Height) : _windowlessSize;

    /// <summary>
    /// What ImGui asks of its textures, queued for the overlay with <paramref name="frame"/> and done as far as ImGui
    /// knows: a new one given an id and filled, a changed one filled again, and one it let go of dropped once the frame
    /// just ended draws nothing with it.
    /// </summary>
    private unsafe void QueueTextureChanges(uint frame)
    {
        ImVector_ImTextureDataPtr_t* textures = ImGui_GetDrawData()->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureData_t* texture = textures->Data[i];
            if (texture->Status == ImTextureStatus_WantCreate)
                ImTextureData_SetTexID(texture, ++_lastTextureId);

            // RGBA8 whole, which is how ImGui's atlas holds it.
            if (texture->Status is ImTextureStatus_WantCreate or ImTextureStatus_WantUpdates)
            {
                byte[] pixels = new ReadOnlySpan<byte>(texture->Pixels, texture->Width * texture->Height * 4).ToArray();
                _textureChanges.Writer.TryWrite(
                    new DebugUITextureChange(texture->TexID, frame, texture->Width, texture->Height, pixels)
                );
                ImTextureData_SetStatus(texture, ImTextureStatus_OK);
            }
            else if (texture->Status == ImTextureStatus_WantDestroy && texture->UnusedFrames > 0)
            {
                _textureChanges.Writer.TryWrite(new DebugUITextureChange(texture->TexID, frame, 0, 0, null));
                ImTextureData_SetTexID(texture, 0);
                ImTextureData_SetStatus(texture, ImTextureStatus_Destroyed);
            }
        }
    }

    /// <summary>
    /// Lays out a field: its label, up to any <c>##</c>, on the left, then the field stretched to the right edge. Returns
    /// the field's hidden ImGui id: the whole label, so fields that share what they show stay apart.
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
            ImGui_TextUnformatted(_text.Add(field.Shown));
            ImGui_SameLine();
        }

        ImGui_SetNextItemWidth(Stretch);

        return _text.Add(field.Id);
    }

    /// <summary>
    /// The text of a tooltip begun, wrapped to a readable width, and the tooltip ended.
    /// </summary>
    private unsafe void WriteTooltip(string text)
    {
        ImGui_PushTextWrapPos(ImGui_GetFontSize() * 30f);
        ImGui_TextUnformatted(_text.Add(text));
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
        if (_levels.Count <= depth)
            _levels.Add(new Level());

        Level level = _levels[depth];
        level.Closed.Clear();
        level.IsScrollable = scrollable;
        level.Indent = 0;
        byte* name = _text.Add(title);
        if (depth == 0)
        {
            _isWindowDrawn = true;

            // Each new window to the right of the last, so none opens on top of another: ImGui keeps a window from its first
            // Begin, so one it does not know is new. Sizes go by the font, which says how big the UI is.
            float width = (scrollable ? ScrollableWindowWidth : FittedWindowWidth) * FontSize;
            if (ImGui_FindWindowByName(name) == null)
            {
                ImGui_SetNextWindowPos(new Vector2(_nextWindowX, FontSize * 2).Im, (int)ImGuiCond_FirstUseEver);
                _nextWindowX += width + FontSize;
            }

            // A window that fits what is in it needs a least width, as its widgets stretch to the width it has.
            if (scrollable)
            {
                ImGui_SetNextWindowSize(
                    new Vector2(width, ScrollableWindowHeight * FontSize).Im,
                    (int)ImGuiCond_FirstUseEver
                );
            }
            else
            {
                ImGui_SetNextWindowSizeConstraints(
                    new Vector2(width, 0).Im,
                    new Vector2(float.MaxValue, float.MaxValue).Im,
                    null,
                    null
                );
            }

            ImGui_Begin(
                name,
                visible,
                scrollable ? (int)ImGuiWindowFlags_None : (int)ImGuiWindowFlags_AlwaysAutoResize
            );

            return;
        }

        // As tall as the parent's room less what followed this view last frame: all of it, the first time. The view's id is
        // the one ImGui gives it, from the title and the window it is in.
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
    private unsafe void ThrowIfOutsideFrame()
    {
        if (_context->WithinFrameScope)
            return;

        throw new InvalidOperationException(
            "The debug UI's widgets are drawn in Update, by a gem made after the UI gem."
        );
    }

    /// <summary>
    /// What is reported, each on a dark plate with its tooltip while the mouse rests on the plate and on no window: text in
    /// the world where the window's camera shows it within <see cref="TextDistance"/>, and the lines down the left of the
    /// picture, oldest first, as many of the last as fit. ImGui draws its background under every window, so the windows
    /// stay readable over what is reported.
    /// </summary>
    private unsafe void DrawReports()
    {
        ImDrawList_t* under = ImGui_GetBackgroundDrawList();
        bool canHover = !ImGui_IsWindowHovered((int)ImGuiHoveredFlags_AnyWindow);
        Vector2 size = GetDisplaySize();

        // Text in the world, as the renderer frames the window's view: the field of view is vertical, and the width follows
        // the target's shape.
        if (_worldTexts.Count > 0 && FindWindowCamera() is (Transform eye, Camera camera))
        {
            Quaternion inverse = Quaternion.Conjugate(eye.Rotation);
            float tanY = MathF.Tan(camera.FieldOfView * (MathF.PI / 360));
            Vector2 tangents = new(tanY * size.X / size.Y, tanY);
            foreach (ShownReport report in _worldTexts)
            {
                // Within reach and in front of the camera, which looks along its +Z with +Y up.
                Vector3 relative = report.Position - eye.Position;
                if (relative.LengthSquared() > TextDistance * TextDistance)
                    continue;

                Vector3 local = Vector3.Transform(relative, inverse);
                if (local.Z <= camera.Near)
                    continue;

                // Across and up the view at its depth, -1 to 1 from edge to edge, then pixels, which count down from the top.
                Vector2 ndc = new Vector2(local.X, local.Y) / (local.Z * tangents);
                if (MathF.Abs(ndc.X) > 1 || MathF.Abs(ndc.Y) > 1)
                    continue;

                Vector2 center = new Vector2((ndc.X * 0.5f) + 0.5f, 0.5f - (ndc.Y * 0.5f)) * size;
                byte* label = _text.Add(report.Label);
                Vector2 extent = ImGui_CalcTextSize(label).Vector;
                DrawPlate(center - (extent * 0.5f), extent, label, report);
            }
        }

        // The lines.
        float lineHeight = ImGui_GetTextLineHeightWithSpacing();
        int fitting = Math.Max(1, (int)((size.Y - (2 * _linesMargin.Y)) / lineHeight));
        Vector2 at = _linesMargin;
        for (int i = Math.Max(0, _screenLines.Count - fitting); i < _screenLines.Count; i++)
        {
            ShownReport line = _screenLines[i];
            byte* label = _text.Add(line.Label);
            DrawPlate(at, ImGui_CalcTextSize(label).Vector, label, line);
            at.Y += lineHeight;
        }

        void DrawPlate(Vector2 corner, Vector2 extent, byte* label, in ShownReport report)
        {
            Vector2 min = corner - _platePadding;
            Vector2 max = corner + extent + _platePadding;
            ImDrawList_AddRectFilledEx(under, min.Im, max.Im, Plate, 3f, (int)ImDrawFlags_None);
            ImDrawList_AddText(under, corner.Im, PackColor(report.Color), label);
            if (report.Tooltip is { } tooltip && canHover && ImGui_IsMouseHoveringRect(min.Im, max.Im))
            {
                ImGui_BeginTooltip();
                WriteTooltip(tooltip);
            }
        }
    }

    /// <summary>
    /// The camera that draws into the window, as the renderer picks it: the first there is, with where it stands; none when
    /// no camera does.
    /// </summary>
    private (Transform Eye, Camera Camera)? FindWindowCamera()
    {
        foreach (EcsChunk<Transform, Camera> chunk in _ecs.Query<Transform, Camera>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.Second[i].Target.IsWindow)
                    return (chunk.First[i], chunk.Second[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// One open <see cref="Begin(string, bool*, bool)"/>: a window, or a view nested in one, and where the views nested in
    /// it closed.
    /// </summary>
    private sealed class Level
    {
        /// <summary>
        /// A nested view's ImGui id; a window needs none.
        /// </summary>
        internal uint Id { get; set; }

        internal bool IsScrollable { get; set; }

        /// <summary>
        /// How many steps its last header indented what follows it.
        /// </summary>
        internal int Indent { get; set; }

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

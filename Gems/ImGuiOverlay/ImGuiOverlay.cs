using Hexa.NET.ImGui;
using HexaGen.Runtime;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Debug;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace ImGuiOverlayGem;

/// <summary>
/// <see cref="IDebugUI"/> drawn with Dear ImGui, straight through <see cref="IRendering"/>: in its Render hook, which runs
/// after the scene was recorded, it ends the ImGui frame the widgets went into, uploads the triangles and textures, and
/// appends plain draw commands over the main window's swapchain image, then starts the next frame. It owns every GPU object
/// it draws with (buffers, textures, sampler, one pipeline per swapchain format) and releases them when it is unloaded or
/// reloaded; the renderer is static and outlives it. What to draw is up to whoever calls <see cref="Debugging.UI"/>, not this gem. Input comes from
/// the frame's events, fed to ImGui in <see cref="Update"/>, which applies them when the next ImGui frame starts. ImGui's
/// own copy and paste (in a text field) go through the <see cref="IClipboard"/> when there is one; without a windowing gem
/// they go nowhere.
/// </summary>
internal sealed unsafe class ImGuiOverlay : IGem, IDebugUI
{
    private const int InputBytes = 256;

    // Text anchored in the world sits on a dark plate, so it reads over anything behind it.
    private const uint WorldTextPlate = 0xC0000000;
    private const uint LinesShadow = 0xFF000000;
    private static readonly Vector2 WorldTextPadding = new(4f, 2f);

    // Where the list down the left of the window starts.
    private static readonly Vector2 LinesMargin = new(8f, 8f);
    private const string FontPath = "Fonts/MontserratMedium.otf";

    /// <summary>
    /// ImGui's -FLT_MIN width: up to the right edge of whatever the field is in.
    /// </summary>
    private const float Stretch = -1.17549435E-38f;

    private const string ContextMenu = "##context";

    private static readonly VertexBufferLayout[] VertexBuffers = [new(0, (uint)sizeof(ImDrawVert))];

    private static readonly VertexAttribute[] VertexAttributes =
    [
        new(0, 0, GpuVertexFormat.Float2, 0),
        new(1, 0, GpuVertexFormat.Float2, 8),
        new(2, 0, GpuVertexFormat.Ubyte4Norm, 16),
    ];

    private readonly IWindowRegistry _windows;
    private readonly IRendering _rendering;
    private readonly ImGuiContextPtr _context;
    private readonly byte[] _input = new byte[InputBytes];
    private readonly Dictionary<uint, (GpuTexture Texture, int Width, int Height)> _textures = [];
    private readonly Dictionary<GpuFormat, GpuPipeline> _pipelines = [];
    private readonly List<Level> _openViews = []; // what Begin opened, by depth; reused frame to frame
    private readonly Dictionary<string, float> _reserved = []; // nested view id: height of what followed it last frame
    private readonly Dictionary<string, DocumentBuffer> _documents = []; // by field id: the text's bytes, encoded once
    private readonly HashSet<string> _positioned = []; // windows given a first position
    private readonly Dictionary<string, int> _tabs = []; // by tab bar id: the tab ImGui showed last frame
    private readonly IClipboard? _clipboard;
    private readonly Assets _assets;
    private readonly string _includes;
    private GpuSampler _sampler;
    private CompiledShader? _vertexShader;
    private CompiledShader? _fragmentShader;
    private uint _generation; // the renderer's device the GPU objects here were made on

    private ImDrawVert[] _vertices = new ImDrawVert[4096];
    private ushort[] _indices = new ushort[8192];
    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;
    private uint _vertexBytes;
    private uint _indexBytes;
    private uint _nextTexture = 1;
    private int _depth;
    private ImFontLoader* _fontLoader; // ImGui's memory: the atlas keeps the pointer
    private GCHandle _font; // the Font asset the loader's callbacks read
    private GCHandle _self; // this overlay, for ImGui's clipboard callbacks
    private GCHandle _pasted; // the clipboard's text as the NUL-terminated UTF-8 ImGui reads, pinned until it asks again

    public ImGuiOverlay(IWindowRegistry windows, IRendering rendering, Assets assets, Project project, IClipboard? clipboard)
    {
        _windows = windows;
        _rendering = rendering;
        _clipboard = clipboard;
        _assets = assets;
        _includes = Path.Combine(project.Resources, "Shaders");

        // cimgui.dll sits with the gems, not next to the exe where the loader looks by default.
        if (!LibraryLoader.CustomLoadFolders.Contains(project.EngineGems))
            LibraryLoader.CustomLoadFolders.Add(project.EngineGems);

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);

        ImGuiIOPtr io = ImGui.GetIO();
        io.IniFilename = null; // nothing to remember between runs: no imgui.ini next to the exe
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;
        ApplyStyle();
        LoadFont(assets);
        if (clipboard is not null)
        {
            _self = GCHandle.Alloc(this);
            ImGuiPlatformIOPtr platform = ImGui.GetPlatformIO();
            platform.PlatformClipboardUserData = (void*)GCHandle.ToIntPtr(_self);
            platform.PlatformSetClipboardTextFn = (delegate* unmanaged[Cdecl]<ImGuiContext*, byte*, void>)&SetClipboard;
            platform.PlatformGetClipboardTextFn = (delegate* unmanaged[Cdecl]<ImGuiContext*, byte*>)&GetClipboard;
        }

        MakeGpuObjects();

        NewFrame(0);
    }

    public void Dispose()
    {
        if (_self.IsAllocated)
            _self.Free();
        if (_pasted.IsAllocated)
            _pasted.Free();
        foreach ((GpuTexture texture, _, _) in _textures.Values)
            _rendering.Release(texture);
        foreach (GpuPipeline pipeline in _pipelines.Values)
            _rendering.Release(pipeline);
        _rendering.Release(_vertexBuffer);
        _rendering.Release(_indexBuffer);
        _rendering.Release(_sampler);
        ImGui.DestroyContext(_context);
        if (_fontLoader != null)
            ImGui.MemFree(_fontLoader);
        if (_font.IsAllocated)
            _font.Free();
    }

    /// <summary>
    /// Feeds the main window's input events to ImGui.
    /// </summary>
    public void Update(in Frame frame)
    {
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        uint main = _windows.Main?.Handle ?? 0;

        foreach (Event inputEvent in frame.Events.Span)
        {
            if (inputEvent.Window != main)
                continue;

            switch (inputEvent.Type)
            {
                case EventType.KeyDown or EventType.KeyUp when inputEvent.Key.ToImGuiKey() is var key && key != ImGuiKey.None:
                    io.AddKeyEvent(key, inputEvent.Type == EventType.KeyDown);
                    break;
                case EventType.TextInput when io.WantTextInput: // only while a field has the keyboard, so keys that open a panel are not typed into it
                    io.AddInputCharactersUTF8(inputEvent.Text);
                    break;
                case EventType.MouseMotion:
                    io.AddMousePosEvent(inputEvent.Value.X, inputEvent.Value.Y);
                    break;
                case EventType.MouseButtonDown or EventType.MouseButtonUp:
                    io.AddMouseButtonEvent((int)inputEvent.Button, inputEvent.Type == EventType.MouseButtonDown); // same order as ImGui's
                    break;
                case EventType.MouseWheel:
                    io.AddMouseWheelEvent(-inputEvent.Value.X, inputEvent.Value.Y);
                    break;
                case EventType.FocusGained or EventType.FocusLost:
                    io.AddFocusEvent(inputEvent.Type == EventType.FocusGained);
                    break;
            }
        }
    }

    /// <summary>
    /// Ends the frame the widgets went into, draws it over the main window, and starts the next.
    /// </summary>
    public void Render(in Frame frame)
    {
        ImGui.SetCurrentContext(_context);
        ImGui.Render();

        ImDrawDataPtr data = ImGui.GetDrawData();
        if (_generation != _rendering.Generation)
            Remake(data);
        UpdateTextures(data);
        if (_windows.Main is { } main && frame.DrawCommands is not null)
            Draw(data, main, frame.DrawCommands);

        NewFrame(frame.Delta);
    }

    public void Begin(string title, bool scrollable = false)
    {
        Begin(title, null, scrollable);
    }

    public void Begin(string title, ref bool visible, bool scrollable = false)
    {
        fixed (bool* closable = &visible)
            Begin(title, closable, scrollable);
    }

    /// <summary>
    /// The window or nested view; a window gets a close button writing to <paramref name="visible"/> unless it is null.
    /// </summary>
    private void Begin(string title, bool* visible, bool scrollable)
    {
        int depth = _depth++;
        if (_openViews.Count <= depth)
            _openViews.Add(new Level());

        Level level = _openViews[depth];
        level.Closed.Clear();
        level.Scrollable = scrollable;
        level.Nested = depth > 0;
        if (!level.Nested)
        {
            level.Id = title;
            if (_positioned.Add(title)) // each new window a step down and right of the last, not on top of it
                ImGui.SetNextWindowPos(new Vector2(20 + (40 * (_positioned.Count - 1)), 20 + (40 * (_positioned.Count - 1))), ImGuiCond.FirstUseEver);
            if (scrollable)
                ImGui.SetNextWindowSize(new Vector2(640, 360), ImGuiCond.FirstUseEver);

            ImGui.Begin(title, visible, scrollable ? ImGuiWindowFlags.None : ImGuiWindowFlags.AlwaysAutoResize);
            return;
        }

        // As tall as the parent's room less what followed this view last frame (0, the first time: all of it).
        level.Id = $"{_openViews[depth - 1].Id}/{title}";
        float reserved = _reserved.GetValueOrDefault(level.Id);
        ImGuiWindowFlags flags = scrollable ? ImGuiWindowFlags.HorizontalScrollbar : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.BeginChild(title, new Vector2(0, -reserved), ImGuiChildFlags.Borders, flags);
    }

    public void End()
    {
        if (_depth == 0)
            return;

        Level level = _openViews[--_depth];

        // What followed each view nested in this one, measured now that everything after them is laid out.
        float end = ImGui.GetCursorPosY();
        foreach ((string id, float closedAt) in level.Closed)
            _reserved[id] = end - closedAt;

        if (!level.Nested)
        {
            ImGui.End();
            return;
        }

        // Scrolled to the bottom: stay there as content is added.
        if (level.Scrollable && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
        _openViews[_depth - 1].Closed.Add((level.Id, ImGui.GetCursorPosY()));
    }

    public void Text(string text, Color color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Packed(color));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public void Text(Vector2 pixel, string text, Color color)
    {
        // ImGui lays out in the window's own units, which a scaled display makes fewer than its pixels.
        Vector2 center = pixel / ImGui.GetIO().DisplayFramebufferScale;
        Vector2 size = ImGui.CalcTextSize(text);
        Vector2 corner = center - (size * 0.5f);

        ImDrawListPtr over = ImGui.GetForegroundDrawList();
        over.AddRectFilled(corner - WorldTextPadding, corner + size + WorldTextPadding, WorldTextPlate, 3f);
        over.AddText(corner, Packed(color), text);
    }

    public void Lines(ReadOnlySpan<DebugLine> lines)
    {
        // As many as fit under each other, the last ones: the first are the oldest.
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        int fitting = Math.Max(1, (int)((ImGui.GetIO().DisplaySize.Y - (2f * LinesMargin.Y)) / lineHeight));
        ImDrawListPtr over = ImGui.GetForegroundDrawList();
        Vector2 at = LinesMargin;
        foreach (DebugLine line in lines[Math.Max(0, lines.Length - fitting)..])
        {
            // No plate behind the list: a dark copy one pixel down and right keeps it readable over a bright scene.
            over.AddText(at + Vector2.One, LinesShadow, line.Text);
            over.AddText(at, Packed(line.Color), line.Text);
            at.Y += lineHeight;
        }
    }

    /// <summary>
    /// The colour as ImGui packs one: alpha, blue, green, red from the top byte down.
    /// </summary>
    private static uint Packed(Color color)
    {
        return ((uint)color.A << 24) | ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;
    }

    public bool Button(string label)
    {
        return ImGui.Button(label);
    }

    public bool Input(string label, ref string text)
    {
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        Utf8.FromUtf16(text, _input.AsSpan(0, InputBytes - 1), out _, out int written); // too long is cut, not an error
        _input[written] = 0;

        string id = LabelField(label);
        bool entered;
        fixed (byte* buffer = _input)
            entered = ImGui.InputText(id, buffer, InputBytes, ImGuiInputTextFlags.EnterReturnsTrue);

        text = Encoding.UTF8.GetString(_input, 0, Array.IndexOf(_input, (byte)0));
        if (entered)
            ImGui.SetKeyboardFocusHere(-1); // keep typing

        return entered;
    }

    public bool Document(string label, ref string text, bool readOnly = false)
    {
        string id = LabelField(label);
        if (!_documents.TryGetValue(id, out DocumentBuffer? document) || !ReferenceEquals(document.Text, text) || document.ReadOnly != readOnly)
            _documents[id] = document = new DocumentBuffer(text, readOnly);

        float height = (document.Lines * ImGui.GetTextLineHeight()) + (ImGui.GetStyle().FramePadding.Y * 2);
        ImGuiInputTextFlags flags = readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;
        bool changed;
        fixed (byte* buffer = document.Bytes)
            changed = ImGui.InputTextMultiline(id, buffer, (nuint)document.Bytes.Length, new Vector2(Stretch, height), flags);

        if (!changed)
            return false;

        text = Encoding.UTF8.GetString(document.Bytes, 0, Array.IndexOf(document.Bytes, (byte)0));
        _documents[id] = new DocumentBuffer(text, readOnly);
        return true;
    }

    public bool Checkbox(string label, ref bool value)
    {
        return ImGui.Checkbox(LabelField(label), ref value);
    }

    public bool Slider(string label, ref float value, float min = 0f, float max = 0f)
    {
        float speed = Math.Max(Math.Abs(value) * 0.01f, 0.1f);
        return ImGui.DragFloat(LabelField(label), ref value, speed, min, max);
    }

    public bool Choice(string label, ref int index, string[] options)
    {
        string id = LabelField(label);
        if (!ImGui.BeginCombo(id, index >= 0 && index < options.Length ? options[index] : ""))
            return false;

        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            if (ImGui.Selectable(options[i], i == index))
                chosen = i;
        }
        ImGui.EndCombo();

        if (chosen == index)
            return false;

        index = chosen;
        return true;
    }

    public bool Tabs(string label, ref int index, string[] options)
    {
        if (!ImGui.BeginTabBar(label))
            return false;

        // ImGui keeps its own selection: the caller's index is pushed only when it is not the tab ImGui last showed.
        string id = $"{_openViews[_depth - 1].Id}/{label}";
        int shown = _tabs.GetValueOrDefault(id, -1);
        int chosen = index;
        for (int i = 0; i < options.Length; i++)
        {
            ImGuiTabItemFlags flags = i == index && index != shown ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (!ImGui.BeginTabItem(options[i], flags))
                continue;

            chosen = i;
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();

        _tabs[id] = chosen;
        if (chosen == index)
            return false;

        index = chosen;
        return true;
    }

    public bool Header(string label)
    {
        return ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
    }

    public void Tooltip(string text)
    {
        if (!ImGui.BeginItemTooltip())
            return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public bool MenuItem(string label)
    {
        // Over the window's own items and nested views too (a document is one), not only its empty space.
        const ImGuiHoveredFlags over = ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.IsWindowHovered(over))
            ImGui.OpenPopup(ContextMenu);

        if (!ImGui.BeginPopup(ContextMenu))
            return false;

        bool clicked = ImGui.MenuItem(label);
        ImGui.EndPopup();
        return clicked;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetClipboard(ImGuiContext* context, byte* text)
    {
        if (Self() is { _clipboard: { } clipboard })
            clipboard.Text = Marshal.PtrToStringUTF8((nint)text) ?? "";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetClipboard(ImGuiContext* context)
    {
        if (Self() is not { _clipboard: { } clipboard } overlay)
            return null;

        if (overlay._pasted.IsAllocated)
            overlay._pasted.Free();

        byte[] bytes = Encoding.UTF8.GetBytes(clipboard.Text + char.MinValue);
        overlay._pasted = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return (byte*)overlay._pasted.AddrOfPinnedObject();
    }

    /// <summary>
    /// The overlay ImGui's clipboard callbacks are for: the one the platform user data names.
    /// </summary>
    private static ImGuiOverlay? Self()
    {
        void* user = ImGui.GetPlatformIO().PlatformClipboardUserData;
        return user == null ? null : GCHandle.FromIntPtr((nint)user).Target as ImGuiOverlay;
    }

    /// <summary>
    /// Lays out a field: its label (up to any <c>##</c>) on the left, then the field stretched to the right edge. Returns
    /// the field's hidden ImGui id: the whole label, so fields sharing a <c>##</c> suffix (the parameters of one pass)
    /// stay apart.
    /// </summary>
    private static string LabelField(string label)
    {
        int hidden = label.IndexOf("##", StringComparison.Ordinal);
        string shown = hidden < 0 ? label : label[..hidden];
        if (shown.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(shown);
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(Stretch);
        return "##" + label;
    }

    /// <summary>
    /// One of the overlay shaders, or null (logged) when it is missing or does not compile: then nothing is drawn.
    /// </summary>
    private static CompiledShader? Compile(Assets assets, IRendering rendering, string path, GpuStage stage, string includes)
    {
        Shader? shader = assets.Load(assets.Find<Shader>("Shaders/" + path));
        if (shader is null)
        {
            Debugging.Log.Error($"Shaders/{path} did not load; the debug UI is not drawn.");
            return null;
        }

        Result<CompiledShader> compiled = rendering.Compile(shader.Text, shader.Path, stage, includes);
        if (compiled.Failed)
            Debugging.Log.Error($"The debug UI is not drawn: {compiled.Message}");

        return compiled.Ok ? compiled.Payload : null;
    }

    /// <summary>
    /// The engine's look: violet glass, lilac edges, pink where something is held, white text, everything rounded.
    /// </summary>
    private static void ApplyStyle()
    {
        ImGui.StyleColorsDark();

        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 10f;
        style.ChildRounding = 8f;
        style.PopupRounding = 8f;
        style.FrameRounding = 6f;
        style.GrabRounding = 6f;
        style.TabRounding = 6f;
        style.ScrollbarRounding = 10f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new Vector2(12f, 10f);
        style.FramePadding = new Vector2(8f, 5f);
        style.ItemSpacing = new Vector2(8f, 6f);
        style.WindowTitleAlign = new Vector2(0.5f, 0.5f);

        Vector4 white = new(1f, 1f, 1f, 1f);
        Vector4 pink = new(1f, 0.52f, 0.86f, 1f);
        Vector4 lilac = new(0.80f, 0.56f, 1f, 1f);
        Vector4 violet = new(0.64f, 0.32f, 0.96f, 1f);
        Vector4 plum = new(0.36f, 0.17f, 0.60f, 1f);
        Vector4 night = new(0.17f, 0.08f, 0.32f, 1f);

        Span<Vector4> colors = style.Colors;
        colors[(int)ImGuiCol.Text] = white;
        colors[(int)ImGuiCol.TextDisabled] = lilac with { W = 0.7f };
        colors[(int)ImGuiCol.WindowBg] = night with { W = 0.90f };
        colors[(int)ImGuiCol.ChildBg] = plum with { W = 0.35f };
        colors[(int)ImGuiCol.PopupBg] = night with { W = 0.97f };
        colors[(int)ImGuiCol.Border] = lilac with { W = 0.85f };
        colors[(int)ImGuiCol.FrameBg] = plum with { W = 0.85f };
        colors[(int)ImGuiCol.FrameBgHovered] = violet with { W = 0.6f };
        colors[(int)ImGuiCol.FrameBgActive] = violet with { W = 0.9f };
        colors[(int)ImGuiCol.TitleBg] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.TitleBgActive] = Vector4.Lerp(violet, pink, 0.45f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = plum with { W = 0.7f };
        colors[(int)ImGuiCol.MenuBarBg] = plum;
        colors[(int)ImGuiCol.ScrollbarBg] = night with { W = 0.4f };
        colors[(int)ImGuiCol.ScrollbarGrab] = violet with { W = 0.8f };
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = lilac;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = pink;
        colors[(int)ImGuiCol.CheckMark] = pink;
        colors[(int)ImGuiCol.SliderGrab] = lilac;
        colors[(int)ImGuiCol.SliderGrabActive] = pink;
        colors[(int)ImGuiCol.Button] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.ButtonHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol.ButtonActive] = pink;
        colors[(int)ImGuiCol.Header] = violet with { W = 0.55f };
        colors[(int)ImGuiCol.HeaderHovered] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.HeaderActive] = pink with { W = 0.9f };
        colors[(int)ImGuiCol.Separator] = lilac with { W = 0.4f };
        colors[(int)ImGuiCol.SeparatorHovered] = lilac;
        colors[(int)ImGuiCol.SeparatorActive] = pink;
        colors[(int)ImGuiCol.ResizeGrip] = lilac with { W = 0.3f };
        colors[(int)ImGuiCol.ResizeGripHovered] = lilac with { W = 0.8f };
        colors[(int)ImGuiCol.ResizeGripActive] = pink;
        colors[(int)ImGuiCol.Tab] = plum;
        colors[(int)ImGuiCol.TabHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol.TabSelected] = violet;
        colors[(int)ImGuiCol.TextSelectedBg] = pink with { W = 0.4f };
        colors[(int)ImGuiCol.NavCursor] = pink;
    }

    /// <summary>
    /// The engine's font for every widget, from its <see cref="Font"/> asset: ImGui asks the loader set up here for each
    /// glyph it first draws and gets the asset's, as rasterised. Without the asset (logged) ImGui keeps its built-in font.
    /// </summary>
    private void LoadFont(Assets assets)
    {
        Font? font = assets.Load(assets.Find<Font>(FontPath));
        if (font is null)
        {
            Debugging.Log.Warn($"{FontPath} did not load; the debug UI keeps ImGui's own font.");
            return;
        }

        _font = GCHandle.Alloc(font);
        _fontLoader = (ImFontLoader*)ImGui.MemAlloc((nuint)sizeof(ImFontLoader));
        *_fontLoader = default;
        _fontLoader->FontSrcContainsGlyph = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, uint, byte>)&ContainsGlyph;
        _fontLoader->FontBakedInit = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, ImFontBaked*, void*, byte>)&InitBaked;
        _fontLoader->FontBakedLoadGlyph = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, ImFontBaked*, void*, uint, ImFontGlyph*, float*, byte>)&LoadGlyph;

        // ImGui copies a source's font data: here that is the asset's handle, which is how the callbacks find the asset.
        // Its size is the line's height, ImGui's meaning of a font size, so widgets lay out around the glyphs as rasterised.
        nint handle = GCHandle.ToIntPtr(_font);
        ImFontConfigPtr config = ImGui.ImFontConfig();
        config.FontLoader = _fontLoader;
        config.FontData = &handle;
        config.FontDataSize = sizeof(nint);
        config.FontDataOwnedByAtlas = false;
        config.SizePixels = font.LineHeight;

        ImGuiIOPtr io = ImGui.GetIO();
        io.FontDefault = io.Fonts.AddFont(config);
        config.Destroy(); // the atlas took a copy
    }

    private static Font? FontOf(ImFontConfig* source)
    {
        return GCHandle.FromIntPtr(*(nint*)source->FontData).Target as Font;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ContainsGlyph(ImFontAtlas* atlas, ImFontConfig* source, uint codepoint)
    {
        return (byte)(FontOf(source) is { } font && font.Glyphs.ContainsKey(codepoint) ? 1 : 0);
    }

    /// <summary>
    /// The line metrics of the font at the size ImGui draws it.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte InitBaked(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData)
    {
        if (FontOf(source) is not { } font)
            return 0;

        float scale = baked->Size / font.LineHeight;
        baked->Ascent = font.Ascent * scale;
        baked->Descent = (font.Ascent - font.LineHeight) * scale;

        return 1;
    }

    /// <summary>
    /// One glyph of the asset into ImGui's atlas, or only its advance when that is all ImGui asks for. Drawn at the
    /// asset's size the glyph's pixels land one to one; at any other size ImGui stretches them.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte LoadGlyph(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData, uint codepoint, ImFontGlyph* glyph, float* advance)
    {
        if (FontOf(source) is not { } font || !font.Glyphs.TryGetValue(codepoint, out Glyph loaded))
            return 0;

        float scale = baked->Size / font.LineHeight;
        if (advance != null)
        {
            *advance = loaded.Advance * scale;
            return 1;
        }

        glyph->Codepoint = codepoint;
        glyph->AdvanceX = loaded.Advance * scale;
        if (loaded.Width == 0 || loaded.Height == 0)
            return 1;

        int rectangle = ImGuiP.ImFontAtlasPackAddRect(atlas, loaded.Width, loaded.Height);
        if (rectangle < 0)
            return 0;

        glyph->X0 = loaded.BearingX * scale;
        glyph->Y0 = MathF.Round(baked->Ascent) - (loaded.BearingY * scale);
        glyph->X1 = glyph->X0 + (loaded.Width * scale);
        glyph->Y1 = glyph->Y0 + (loaded.Height * scale);
        glyph->Visible = 1;
        glyph->PackId = rectangle;

        fixed (byte* pixels = &font.Pixels[((loaded.Y * font.Width) + loaded.X) * 4])
            ImGuiP.ImFontAtlasBakedSetFontGlyphBitmap(atlas, baked, source, glyph, ImGuiP.ImFontAtlasPackGetRect(atlas, rectangle), pixels, ImTextureFormat.Rgba32, font.Width * 4);

        return 1;
    }

    private void NewFrame(float delta)
    {
        Size size = _windows.Main?.Size ?? default, pixels = _windows.Main?.PixelSize ?? default;

        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(size.Width, size.Height);
        if (size.Width > 0 && size.Height > 0)
            io.DisplayFramebufferScale = new Vector2((float)pixels.Width / size.Width, (float)pixels.Height / size.Height);
        io.DeltaTime = Math.Max(delta, 1e-4f);

        ImGui.NewFrame();
    }

    /// <summary>
    /// ImGui's draw lists as one upload each of vertices (in window pixels) and indices, then one render pass of draws over the window.
    /// </summary>
    private void Draw(ImDrawDataPtr data, IWindow window, RenderCommands commands)
    {
        int vertexCount = data.TotalVtxCount, indexCount = data.TotalIdxCount;
        Size target = window.PixelSize;
        if (vertexCount == 0 || target.Width <= 0 || target.Height <= 0 || Pipeline(window.Handle) is not { IsValid: true } pipeline)
            return;

        if (_vertices.Length < vertexCount)
            Array.Resize(ref _vertices, Math.Max(vertexCount, _vertices.Length * 2));
        if (_indices.Length < indexCount)
            Array.Resize(ref _indices, Math.Max(indexCount, _indices.Length * 2));

        Vector2 origin = data.DisplayPos;
        Vector2 scale = data.FramebufferScale;
        int vertexBase = 0, indexBase = 0;
        for (int listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = data.CmdLists[listIndex];
            new ReadOnlySpan<ImDrawVert>(list.VtxBuffer.Data, list.VtxBuffer.Size).CopyTo(_vertices.AsSpan(vertexBase));
            for (int vertex = vertexBase; vertex < vertexBase + list.VtxBuffer.Size; vertex++)
                _vertices[vertex].Pos = (_vertices[vertex].Pos - origin) * scale; // points to pixels

            new ReadOnlySpan<ushort>(list.IdxBuffer.Data, list.IdxBuffer.Size).CopyTo(_indices.AsSpan(indexBase));
            vertexBase += list.VtxBuffer.Size;
            indexBase += list.IdxBuffer.Size;
        }

        Upload(ref _vertexBuffer, ref _vertexBytes, GpuBufferUsage.Vertex, MemoryMarshal.AsBytes(_vertices.AsSpan(0, vertexBase)));
        Upload(ref _indexBuffer, ref _indexBytes, GpuBufferUsage.Index, MemoryMarshal.AsBytes(_indices.AsSpan(0, indexBase)));

        commands.BeginRenderPass(GpuTexture.Window(window.Handle), GpuLoad.Load);
        commands.SetViewport(new Rectangle(0, 0, target.Width, target.Height));
        commands.BindPipeline(pipeline);
        commands.BindVertexBuffers(0, [_vertexBuffer]);
        commands.BindIndexBuffer(_indexBuffer, wide: false);
        commands.Push(GpuStage.Vertex, new Vector4(1f / target.Width, 1f / target.Height, 0f, 0f));

        uint bound = 0;
        vertexBase = indexBase = 0;
        for (int listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = data.CmdLists[listIndex];
            for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmd cmd = list.CmdBuffer.Data[commandIndex];
                ImTextureID id = cmd.TexRef.TexData != null ? cmd.TexRef.TexData->TexID : cmd.TexRef.TexID;
                if (cmd.UserCallback != null || cmd.ElemCount == 0 || !_textures.TryGetValue((uint)id.Handle, out (GpuTexture Texture, int, int) texture))
                    continue;

                Vector2 min = (new Vector2(cmd.ClipRect.X, cmd.ClipRect.Y) - origin) * scale;
                Vector2 max = (new Vector2(cmd.ClipRect.Z, cmd.ClipRect.W) - origin) * scale;
                Rectangle clip = Rectangle.Intersect(
                    Rectangle.FromLTRB((int)MathF.Floor(min.X), (int)MathF.Floor(min.Y), (int)MathF.Ceiling(max.X), (int)MathF.Ceiling(max.Y)),
                    new Rectangle(0, 0, target.Width, target.Height));
                if (clip.Width <= 0 || clip.Height <= 0)
                    continue;

                if (bound != (uint)id.Handle)
                {
                    commands.BindTextures(GpuStage.Fragment, 0, [new GpuBinding(Texture: texture.Texture, Sampler: _sampler)]);
                    bound = (uint)id.Handle;
                }

                commands.SetScissor(clip);
                commands.DrawIndexed(cmd.ElemCount, (uint)indexBase + cmd.IdxOffset, vertexBase + (int)cmd.VtxOffset);
            }

            vertexBase += list.VtxBuffer.Size;
            indexBase += list.IdxBuffer.Size;
        }

        commands.EndRenderPass();
    }

    /// <summary>
    /// The pipeline for the window's swapchain format, made the first time; none when the shaders did not compile.
    /// </summary>
    private GpuPipeline Pipeline(uint window)
    {
        if (_vertexShader is null || _fragmentShader is null)
            return default;

        GpuFormat format = _rendering.WindowFormat(window);
        if (format == GpuFormat.Invalid)
            return default;
        if (_pipelines.TryGetValue(format, out GpuPipeline pipeline))
            return pipeline;

        pipeline = _rendering.CreatePipeline(new PipelineDesc(_vertexShader, _fragmentShader, format)
        {
            Buffers = VertexBuffers,
            Attributes = VertexAttributes,
            Cull = GpuCull.None,
            Blends = [GpuBlend.Alpha],
        });
        _pipelines[format] = pipeline;

        return pipeline;
    }

    /// <summary>
    /// The bytes into the buffer, made (again) bigger first when they do not fit.
    /// </summary>
    private void Upload(ref GpuBuffer buffer, ref uint size, GpuBufferUsage usage, ReadOnlySpan<byte> bytes)
    {
        if (!buffer.IsValid || size < bytes.Length)
        {
            _rendering.Release(buffer);
            size = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes.Length, 16 * 1024));
            buffer = _rendering.CreateBuffer(usage, size);
        }

        _rendering.Upload(buffer, 0, bytes);
    }

    /// <summary>
    /// The shaders, compiled for the renderer's format, and the sampler; the pipelines and buffers are made when first drawn.
    /// </summary>
    private void MakeGpuObjects()
    {
        _generation = _rendering.Generation;
        _vertexShader = Compile(_assets, _rendering, "Overlay/Overlay.vert.hlsl", GpuStage.Vertex, _includes);
        _fragmentShader = Compile(_assets, _rendering, "Overlay/Overlay.frag.hlsl", GpuStage.Fragment, _includes);
        _sampler = _rendering.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp));
    }

    /// <summary>
    /// The renderer made its device again: everything made on the old one is gone (nothing to release), so the shaders
    /// (the format may differ), the sampler, the pipelines, the buffers and ImGui's textures are made again.
    /// </summary>
    private void Remake(ImDrawDataPtr data)
    {
        _pipelines.Clear();
        _vertexBuffer = default;
        _indexBuffer = default;
        _vertexBytes = _indexBytes = 0;
        MakeGpuObjects();

        foreach (uint id in _textures.Keys.ToArray())
            _textures[id] = (default, 0, 0);

        ImVector<ImTextureDataPtr>* textures = data.Handle->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureDataPtr texture = textures->Data[i];
            if (texture.Status == ImTextureStatus.Ok)
                UpdateTexture((uint)texture.TexID.Handle, texture);
        }
    }

    /// <summary>
    /// ImGui (1.92+) asks the backend to create, update and destroy its textures: each is a GPU texture here, by an id of
    /// our own, its pixels uploaded as RGBA8 whenever ImGui changes them.
    /// </summary>
    private void UpdateTextures(ImDrawDataPtr data)
    {
        ImVector<ImTextureDataPtr>* textures = data.Handle->Textures;
        if (textures == null)
            return;

        for (int i = 0; i < textures->Size; i++)
        {
            ImTextureDataPtr texture = textures->Data[i];
            switch (texture.Status)
            {
                case ImTextureStatus.WantCreate:
                    uint id = _nextTexture++;
                    _textures[id] = (default, 0, 0);
                    UpdateTexture(id, texture);
                    texture.SetTexID(new ImTextureID(id));
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;
                case ImTextureStatus.WantUpdates:
                    UpdateTexture((uint)texture.TexID.Handle, texture);
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;
                case ImTextureStatus.WantDestroy when texture.UnusedFrames > 0:
                    if (_textures.Remove((uint)texture.TexID.Handle, out (GpuTexture Texture, int, int) gone))
                        _rendering.Release(gone.Texture);
                    texture.SetTexID(ImTextureID.Null);
                    texture.SetStatus(ImTextureStatus.Destroyed);
                    break;
            }
        }
    }

    /// <summary>
    /// The texture's pixels into its GPU texture, made again when the size changed.
    /// </summary>
    private void UpdateTexture(uint id, ImTextureDataPtr texture)
    {
        if (!_textures.TryGetValue(id, out (GpuTexture Texture, int Width, int Height) known))
            return;

        if (!known.Texture.IsValid || known.Width != texture.Width || known.Height != texture.Height)
        {
            _rendering.Release(known.Texture);
            known = (_rendering.CreateTexture(new TextureDesc(GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampler, (uint)texture.Width, (uint)texture.Height)), texture.Width, texture.Height);
            _textures[id] = known;
        }

        _rendering.Upload(new TextureRegion(known.Texture), Rgba(texture));
    }

    /// <summary>
    /// The texture's pixels as RGBA8: as they are, or Alpha8 made white with the coverage as alpha.
    /// </summary>
    private static byte[] Rgba(ImTextureDataPtr texture)
    {
        int count = texture.Width * texture.Height;
        byte* pixels = texture.Pixels;
        if (texture.BytesPerPixel == 4)
            return new ReadOnlySpan<byte>(pixels, count * 4).ToArray();

        byte[] rgba = new byte[count * 4];
        for (int pixel = 0; pixel < count; pixel++)
        {
            rgba[pixel * 4] = rgba[(pixel * 4) + 1] = rgba[(pixel * 4) + 2] = 255;
            rgba[(pixel * 4) + 3] = pixels[pixel];
        }

        return rgba;
    }

    /// <summary>
    /// One open <see cref="Begin"/>: a window, or a view nested in one, and the views nested in it that closed where.
    /// </summary>
    private sealed class Level
    {
        public string Id { get; set; } = "";

        public bool Nested { get; set; }

        public bool Scrollable { get; set; }

        public List<(string Id, float ClosedAt)> Closed { get; } = [];
    }

    /// <summary>
    /// A document's text as the NUL-terminated UTF-8 ImGui edits, with room to type into unless read-only.
    /// </summary>
    private sealed class DocumentBuffer
    {
        public DocumentBuffer(string text, bool readOnly)
        {
            Text = text;
            ReadOnly = readOnly;
            int length = Encoding.UTF8.GetByteCount(text);
            Bytes = new byte[length + (readOnly ? 1 : 4096)];
            Encoding.UTF8.GetBytes(text, Bytes);
            Lines = 1 + text.AsSpan().Count('\n');
        }

        public string Text { get; }

        public bool ReadOnly { get; }

        public byte[] Bytes { get; }

        public int Lines { get; }
    }
}

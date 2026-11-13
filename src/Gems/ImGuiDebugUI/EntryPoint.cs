using Magic.Assets;
using Magic.Ecs;
using Magic.Input;
using Magic.Platform;
using Magic.Projects;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The gem: the debug UI, drawn with Dear ImGui.
/// </summary>
/// <remarks>
/// <see cref="DebugUI"/> holds ImGui's frame from Update to LateUpdate, so the widgets that gems after this one make in
/// their Update go into it. <see cref="Renderer"/> draws each frame over the window's image.
/// <see cref="LogSink"/> shows the lines logged to show on screen.
/// </remarks>
public sealed class EntryPoint : IGem, IDisposable
{
    public EntryPoint(
        ImGuiDebugUISettings settings,
        App app,
        AssetManager assets,
        IEcs ecs,
        IInput? input = null,
        IClipboard? clipboard = null
    )
    {
        // What gems report and what is logged to show both go into one set of reports, which the UI draws.
        Reports reports = new();
        Settings = settings;
        DebugUI = new DebugUI(settings, app, ecs, reports, input, clipboard);
        LogSink = new LogSink(settings, reports);
        Renderer = new Renderer(assets);
    }

    /// <summary>
    /// How the debug UI's text and windows look, and what of the log shows on screen. Game thread.
    /// </summary>
    public ImGuiDebugUISettings Settings { get; }

    public DebugUI DebugUI { get; }

    public LogSink LogSink { get; }

    public Renderer Renderer { get; }

    public void Dispose() => DebugUI.Dispose();

    public void Update(in Frame frame) => DebugUI.BeginFrame(frame);

    public void LateUpdate(in Frame frame)
    {
        DebugUI.EndFrame(frame);
        Renderer.Publish(frame.Index);
    }
}

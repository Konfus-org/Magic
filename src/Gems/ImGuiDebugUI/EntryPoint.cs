using Magic.Assets;
using Magic.Ecs;
using Magic.Input;
using Magic.Platform;
using Magic.Projects;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The gem: the debug UI, drawn with Dear ImGui. <see cref="DebugUI"/> holds ImGui's frame from Update to LateUpdate, so
/// the widgets gems made after this one call in their Update go into it; <see cref="Renderer"/> draws each frame ImGui
/// ended over the window's image; and <see cref="LogSink"/> shows the lines logged to show on screen. It is public
/// because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
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

    /// <summary>
    /// The widgets, what gems report, and ImGui's frame.
    /// </summary>
    public DebugUI DebugUI { get; }

    /// <summary>
    /// The lines logged to show on screen.
    /// </summary>
    public LogSink LogSink { get; }

    /// <summary>
    /// ImGui's frames over the window's image.
    /// </summary>
    public Renderer Renderer { get; }

    public void Dispose() => DebugUI.Dispose();

    public void Update(in Frame frame) => DebugUI.BeginFrame(frame);

    public void LateUpdate(in Frame frame)
    {
        DebugUI.EndFrame(frame);
        Renderer.Publish(frame.Index);
    }
}

using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Numerics;

namespace DebugToolsGem;

/// <summary>
/// The debug UI in the world: everything reported with a position (<see cref="Debugging.UI.Text(Vector3, string, Color?)"/>,
/// <see cref="Debugging.UI.Warning"/>, <see cref="Debugging.UI.Error"/>) is drawn as text at that place, in every view
/// of the main window that has it in front of its camera and within <see cref="Debugging.UI.TextDistance"/> metres.
/// It reads the cameras itself and asks nothing of the renderer. Shown while <see cref="Debugging.UI.Enabled"/>: always
/// in a Debug build, in a Release build only while the debug UI is on. Runs after the render system, so what that
/// reports this frame shows this frame.
/// </summary>
internal sealed class DebugUI3DSystem : ISystem
{
    private readonly IWindowRegistry? _windows;
    private readonly RenderSettings _settings;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
    private readonly QueryChunkAction<Camera, WorldTransform> _collectViews;
    private readonly List<DebugView> _views = [];
    private readonly List<DebugEntry> _entries = [];
    private Size _window;
    private uint _main;

    public DebugUI3DSystem(IEcs ecs, IWindowRegistry? windows, RenderSettings settings)
    {
        _windows = windows;
        _settings = settings;
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _collectViews = CollectViews;
    }

    public void Dispose()
    {
        _cameras.Dispose();
    }

    public UpdateType Phase => UpdateType.Overlay;

    public void Run(in Frame frame)
    {
        // Taken whether shown or not: what is no longer reported has to go either way.
        _entries.Clear();
        Debugging.UI.Entries.Take(Debugging.UI.Now, positioned: true, _entries);
        if (_entries.Count == 0 || !Debugging.UI.Enabled || _windows?.Main is not { IsOpen: true } main)
            return;

        _window = main.PixelSize;
        _main = main.Handle;
        if (_window.Width <= 0 || _window.Height <= 0)
            return;

        _views.Clear();
        _cameras.Run(_collectViews);
        foreach (DebugEntry entry in _entries)
        {
            foreach (DebugView view in _views)
            {
                if (entry.Position is { } position && view.Project(position, Debugging.UI.TextDistance, out Vector2 pixel))
                    Debugging.UI.Text(pixel, entry.Text, entry.Color);
            }
        }
    }

    /// <summary>
    /// The views of the cameras that draw into the main window: each one's rectangle in the window's pixels and its
    /// view-projection. The scene may be drawn at another resolution and stretched over the window, so the shape of
    /// the view is taken from that resolution and its place from the window.
    /// </summary>
    private void CollectViews(ReadOnlySpan<Handle> entities, Span<Camera> cameras, Span<WorldTransform> worlds)
    {
        Size drawn = _settings.Resolution is { Width: > 0, Height: > 0 } resolution ? resolution : _window;
        for (int i = 0; i < cameras.Length; i++)
        {
            RenderTarget target = cameras[i].Target;
            if (target.IsTexture || (target.Window != 0 && target.Window != _main))
                continue;

            Rectangle shape = cameras[i].Viewport.ToPixels(drawn.Width, drawn.Height);
            Rectangle place = cameras[i].Viewport.ToPixels(_window.Width, _window.Height);
            float aspect = shape.Height > 0 ? (float)shape.Width / shape.Height : 1f;
            Matrix4x4 world = worlds[i].Value;
            _views.Add(new DebugView(cameras[i].ViewProjection(world, aspect), world.Translation, place));
        }
    }
}

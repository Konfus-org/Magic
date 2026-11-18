using System.Numerics;
using Magic.CommandLine;
using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.DeferredRendering;

/// <summary>
/// The directional light that lights the frame: the unit direction towards it, its colour times its intensity, in lux
/// above the air, and what it is to the sky.
/// </summary>
public readonly record struct SunLight(Vector3 Towards, Vector3 Illuminance, DirectionalLightMode Mode);

/// <summary>
/// What the renderer shows and what lights it, under the console path <c>DeferredRendering</c>.
/// </summary>
/// <remarks>
/// The keys of <see cref="DeferredRenderingSettings"/> and the Rendering window change the view, and so does the
/// console, as <c>set DeferredRendering.RenderView Normal</c>. The view is state of the run and is not saved. The game
/// thread writes it, and the render thread reads it. Game thread.
/// </remarks>
[CliPath("DeferredRendering")]
public sealed class RendererState
{
    private readonly IEcs _ecs;

    internal RendererState(IEcs ecs) => _ecs = ecs;

    /// <summary>
    /// The part of the frame the window shows: Final is the finished frame, any other one part of the GBuffer or the
    /// light alone.
    /// </summary>
    public DebugView RenderView { get; set; } = DebugView.Final;

    /// <summary>
    /// Metres from the camera at which the Depth view turns black; nearer is lighter, on a log scale.
    /// </summary>
    public float DepthViewFar { get; set; } = 1000;

    /// <summary>
    /// Whether the Rendering window, which chooses the view, is open. It needs a gem that provides the debug UI.
    /// </summary>
    public bool IsRenderWindowOpen { get; set; }

    /// <summary>
    /// The light that the frame is lit by, as <see cref="Renderer.GetSun"/> picks it of the world's directional
    /// lights, and <see cref="Renderer.NoSun"/> while the world has none.
    /// </summary>
    /// <remarks>
    /// A tool that lights the same scene reads <c>query DeferredRendering.Sun</c> instead of working the rule out
    /// again. The values are the live world's, in Magic's own units: metres and lux, left-handed with +Y up. The
    /// lights are as the world holds them at the call, so a tool that wants another hour holds the day cycle at it
    /// first (<c>set Day.Hour</c>). The illuminance is the light above the air; what the air leaves of it at a point
    /// is in the sky's transmittance table on the GPU, which this does not read.
    /// </remarks>
    public SunLight Sun
    {
        get
        {
            EcsSnapshot<Transform, DirectionalLight> lights = new();
            lights.Fill(_ecs);

            return Renderer.GetSun(lights.First, lights.Second) is { } sun
                ? new SunLight(sun.Direction, sun.Illuminance, sun.Mode)
                : new SunLight(Renderer.NoSun.Direction, Renderer.NoSun.Illuminance, DirectionalLightMode.Default);
        }
    }
}

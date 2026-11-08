using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;

namespace Magic.GIRendering;

/// <summary>
/// What the output shows of a frame's surfaces, the twin of <c>ShowSurface.slang</c>'s.
/// </summary>
internal enum DebugView : uint
{
    /// <summary>
    /// The finished frame.
    /// </summary>
    Final,

    /// <summary>
    /// The surface's base colour.
    /// </summary>
    Albedo,

    /// <summary>
    /// The surface's world normal, each axis from -1 to 1 shown from black to full.
    /// </summary>
    Normal,

    /// <summary>
    /// How far the surface is from the camera, on a log scale: white at the camera.
    /// </summary>
    Depth,

    Roughness,

    Metalness,

    /// <summary>
    /// The material's own occlusion.
    /// </summary>
    Occlusion,

    /// <summary>
    /// How far the surface moved on the screen since last frame.
    /// </summary>
    Velocity,

    /// <summary>
    /// The finished frame with the probe grid over it, on a GPU that traces rays: each probe a sphere showing the light
    /// it holds from each direction.
    /// </summary>
    Probes,

    /// <summary>
    /// On a GPU that traces rays, the traced diffuse light as shading reads it: what a white diffuse surface would
    /// reflect of the light that reaches it indirectly.
    /// </summary>
    TracedDiffuse,

    /// <summary>
    /// What smooth surfaces reflect, as traced and blended over frames at half the size, through the tonemap; black
    /// where nothing is traced.
    /// </summary>
    Reflections,
}

/// <summary>
/// The output drawn from the lit picture, at the metered exposure through the tonemap, or from one of the GBuffer's
/// properties or the traced reflections when a debug view asks.
/// </summary>
internal sealed class SurfaceView(AssetManager assets)
{
    private readonly ShaderEntry _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");

    public ImmutableArray<ShaderEntry> Shaders => [_showShader];

    /// <summary>
    /// The output drawn as the debug view asks, from the traced diffuse light and the traced reflections' light where
    /// there are any.
    /// </summary>
    public void Record(in RenderView view, DebugView debugView, TracedDiffuseBuffers? diffuse, GpuTexture reflections)
    {
        ComputePass show = view
            .Graph.Compute(
                "Show surface",
                _showShader,
                new GpuConstants(view.Constants.Id, (uint)debugView, reflections.Id, diffuse?.Constants.Id ?? 0)
            )
            .Reads(
                view.Constants,
                view.ExposureMeter,
                view.Visibility,
                view.Depth,
                view.GBufferNormal,
                view.GBufferBase,
                view.GBufferSurface,
                view.Velocity,
                view.Hdr
            );
        if (diffuse is { } traced)
            show.Reads(traced.Constants, traced.Light);

        if (!reflections.IsNone)
            show.Reads(reflections);

        show.Writes(view.Output).DispatchOver(view.Width, view.Height);
    }
}

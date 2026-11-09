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
    /// What smooth surfaces reflect, as traced and blended over frames at half the size, finished by the post
    /// effects; black where nothing is traced.
    /// </summary>
    Reflections,
}

/// <summary>
/// The output drawn from the picture as its effects finished it, or from one of the GBuffer's properties when a debug
/// view asks; a view of the traced light alone is first written into the picture, so the effects finish it as they would
/// the picture.
/// </summary>
internal sealed class SurfaceView(AssetManager assets)
{
    private readonly ShaderEntry _lightShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowLight");
    private readonly ShaderEntry _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");

    public ImmutableArray<ShaderEntry> Shaders => [_lightShader, _showShader];

    /// <summary>
    /// The traced diffuse light or the traced reflections' light written into the picture in place of the lit one, when
    /// the debug view shows one, black where none was traced.
    /// </summary>
    public void RecordLight(
        in RenderView view,
        DebugView debugView,
        TracedDiffuseBuffers? diffuse,
        GpuTexture reflections
    )
    {
        if (debugView is not (DebugView.TracedDiffuse or DebugView.Reflections))
            return;

        ComputePass show = view
            .Graph.Compute("Show light", _lightShader, new GpuConstants(view.Constants.Id, (uint)debugView))
            .Reads(view.Constants, view.Visibility, view.Depth, view.GBufferNormal);
        if (diffuse is { } traced)
            show.Reads(traced.Light);

        if (!reflections.IsNone)
            show.Reads(reflections);

        show.Writes(view.Hdr).DispatchOver(view.Width, view.Height);
    }

    /// <summary>
    /// The output drawn as the debug view asks.
    /// </summary>
    public void Record(in RenderView view, DebugView debugView) =>
        view
            .Graph.Compute("Show surface", _showShader, new GpuConstants(view.Constants.Id, (uint)debugView))
            .Reads(
                view.Constants,
                view.Visibility,
                view.Depth,
                view.GBufferNormal,
                view.GBufferBase,
                view.GBufferSurface,
                view.Velocity,
                view.Hdr
            )
            .Writes(view.Output)
            .DispatchOver(view.Width, view.Height);
}

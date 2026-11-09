using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;

namespace Magic.Deferred;

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
    /// The indirect diffuse light as shading reads it, where something traces it: what a white diffuse surface would
    /// reflect of the light that reaches it indirectly.
    /// </summary>
    IndirectDiffuse,

    /// <summary>
    /// What smooth surfaces reflect, where something traces it at half the size, finished by the post
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
    /// The indirect diffuse light or the reflections' light written into the picture in place of the lit one, when
    /// the debug view shows one, black where none was traced.
    /// </summary>
    public void RecordLight(in RenderView view, DebugView debugView, ImmutableArray<GpuResource> indirect)
    {
        if (debugView is not (DebugView.IndirectDiffuse or DebugView.Reflections))
            return;

        ComputePass show = view
            .Graph.Compute(
                "Show light",
                _lightShader,
                new GpuConstants(view.Gpu.Frame.Id, (uint)debugView),
                RenderStage.Shade
            )
            .Reads(view.Gpu.Frame, view.Visibility, view.Gpu.Depth, view.Gpu.GBufferNormal)
            .Reads(indirect.AsSpan());
        show.Writes(view.Gpu.Hdr).DispatchOver(view.Gpu.Width, view.Gpu.Height);
    }

    /// <summary>
    /// The output drawn as the debug view asks.
    /// </summary>
    public void Record(in RenderView view, DebugView debugView) =>
        view
            .Graph.Compute(
                "Show surface",
                _showShader,
                new GpuConstants(view.Gpu.Frame.Id, (uint)debugView),
                RenderStage.Post
            )
            .Reads(
                view.Gpu.Frame,
                view.Visibility,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferBase,
                view.Gpu.GBufferSurface,
                view.Gpu.Velocity,
                view.Gpu.Hdr
            )
            .Writes(view.Gpu.Output)
            .DispatchOver(view.Gpu.Width, view.Gpu.Height);
}

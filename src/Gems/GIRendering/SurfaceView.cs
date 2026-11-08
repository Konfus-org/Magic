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
}

/// <summary>
/// The output drawn from the lit picture, or from one of the GBuffer's properties when a debug view asks.
/// </summary>
internal sealed class SurfaceView(AssetManager assets)
{
    private readonly ShaderEntry _showShader = PassShaders.GetEntry(assets, "ShowSurface", "ShowSurface");

    public ImmutableArray<ShaderEntry> Shaders => [_showShader];

    public void Record(in RenderView view, DebugView debugView, float exposure)
    {
        view.Graph.Compute(
                "Show surface",
                _showShader,
                new GpuConstants(
                    view.Constants.Id,
                    (uint)debugView,
                    view.Hdr.Id,
                    BitConverter.SingleToUInt32Bits(exposure)
                )
            )
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
}

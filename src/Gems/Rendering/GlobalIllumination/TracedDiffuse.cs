using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// Traces the light that reaches each surface indirectly from near it, on a GPU that traces rays.
/// </summary>
/// <remarks>
/// Two rays go from a pixel of each traced pixel's footprint. The lights and the probes light them where they land, and
/// the probes' light is used where they reach nothing within range. The light is filtered over neighbours and blended
/// over frames into the light that shading reads.
/// </remarks>
internal sealed class TracedDiffuse(AssetManager assets)
{
    private static readonly string[] _blendNames = ["Diffuse light 0", "Diffuse light 1"];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "TraceDiffuse", "TraceDiffuse") with
    {
        TypeArguments = [GpuView.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "TraceDiffuse", "FilterDiffuse");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceDiffuse", "BlendDiffuse");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _filterShader, _blendShader];

    /// <summary>
    /// Asks for the textures at the traced size, the blend's history holding the view as
    /// <paramref name="hasHistory"/> says.
    /// </summary>
    /// <remarks>
    /// The blended light is the indirect diffuse light that shading reads.
    /// </remarks>
    public static Result<TracedDiffuseConstants> Prepare(in GIView view, uint frameIndex, bool hasHistory)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = graph.Texture(view.GetTraced("Traced diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> filtered = graph.Texture(view.GetTraced("Filtered diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<TracedBlendConstants> blend = TracedBlend.Prepare(view, _blendNames, frameIndex, hasHistory);
        if (Result.FindFailure(traced, filtered, blend) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new TracedDiffuseConstants
            {
                Traced = traced.Value,
                Filtered = filtered.Value,
                Blend = blend.Value,
            }
        );
    }

    /// <summary>
    /// Traces rays from the view's surfaces through the world, then filters them and blends them with the history.
    /// </summary>
    /// <remarks>
    /// The lights, the sky and the probes light the rays. The blend also keeps the view depth for the next frame.
    /// </remarks>
    public void Record(in GIView view, in ProbeGridConstants grid, in GIConstants gi)
    {
        RenderGraph graph = view.Graph;
        TracedDiffuseConstants diffuse = gi.Diffuse;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        graph
            .Compute("Trace diffuse", _traceShader, header, RenderStages.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal)
            .Reads(grid.Probes, grid.Depth)
            .Writes(diffuse.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Filter diffuse", _filterShader, header, RenderStages.Light)
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, diffuse.Traced)
            .Writes(diffuse.Filtered)
            .DispatchOver(width, height);
        graph
            .Compute("Blend diffuse", _blendShader, header, RenderStages.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.Velocity,
                diffuse.Filtered,
                diffuse.Blend.LightHistory,
                gi.DepthHistory
            )
            .Writes(diffuse.Blend.Light, gi.Depth)
            .DispatchOver(width, height);
    }
}

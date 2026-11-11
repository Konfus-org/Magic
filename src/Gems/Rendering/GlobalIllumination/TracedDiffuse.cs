using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// On a GPU that traces rays, the light that reaches each surface indirectly, traced near it: two rays from a pixel of
/// each traced pixel's footprint, lit where they land by the lights and the probes, and the probes' light where they
/// reach nothing within range; filtered over neighbours and blended over frames, into the light shading reads.
/// </summary>
internal sealed class TracedDiffuse(AssetManager assets)
{
    private static readonly string[] _blendNames =
    [
        "Diffuse light 0",
        "Diffuse light 1",
        "Diffuse depth 0",
        "Diffuse depth 1",
    ];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "TraceDiffuse", "TraceDiffuse") with
    {
        TypeArguments = [GpuView.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "TraceDiffuse", "FilterDiffuse");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceDiffuse", "BlendDiffuse");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _filterShader, _blendShader];

    /// <summary>
    /// The textures asked for at the traced size; the blended light is the indirect diffuse light shading reads.
    /// </summary>
    public static Result<TracedDiffuseConstants> Prepare(in GIView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = graph.Texture(view.GetTraced("Traced diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> filtered = graph.Texture(view.GetTraced("Filtered diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<TracedBlendConstants> blend = TracedBlend.Prepare(view, _blendNames, frameIndex);
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
    /// The rays traced from the view's surfaces through the world, lit by the lights, the sky and the probes, then
    /// filtered and blended with the history.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes, in TracedDiffuseConstants diffuse)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        graph
            .Compute("Trace diffuse", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal)
            .Reads(probes.Probes, probes.Depth)
            .Writes(diffuse.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Filter diffuse", _filterShader, header, RenderStage.Light)
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, diffuse.Traced)
            .Writes(diffuse.Filtered)
            .DispatchOver(width, height);
        graph
            .Compute("Blend diffuse", _blendShader, header, RenderStage.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.Velocity,
                diffuse.Filtered,
                diffuse.Blend.LightHistory,
                diffuse.Blend.DepthHistory
            )
            .Writes(diffuse.Blend.Light, diffuse.Blend.Depth)
            .DispatchOver(width, height);
    }
}

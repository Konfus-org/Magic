using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// A frame's traced diffuse light, at half the view's size: the rays' light, filtered, and blended with the history.
/// </summary>
internal readonly record struct TracedDiffuseBuffers(GpuTexture Traced, GpuTexture Filtered, TracedBlend Blend)
{
    public TracedDiffuseConstants Constants =>
        new()
        {
            Traced = Traced.Id,
            Filtered = Filtered.Id,
            Blend = Blend.Constants,
        };
}

/// <summary>
/// On a GPU that traces rays, the light that reaches each surface indirectly, traced near it: two rays from a pixel of
/// each 2 by 2, lit where they land by the lights and the probes, and the probes' light where they reach nothing within
/// range; filtered over neighbours and blended over frames, into the light shading reads.
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
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "TraceDiffuse", "FilterDiffuse");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceDiffuse", "BlendDiffuse");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _filterShader, _blendShader];

    /// <summary>
    /// The textures asked for at half the view's size; the blended light is the indirect diffuse light shading reads.
    /// </summary>
    public static Result<TracedDiffuseBuffers> Prepare(in GIView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = graph.Texture(view.GetTraced("Traced diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> filtered = graph.Texture(view.GetTraced("Filtered diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<TracedBlend> blend = TracedBlend.Prepare(view, _blendNames, frameIndex);
        foreach (Result made in (ReadOnlySpan<Result>)[traced, filtered, blend])
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        return Result.Success(new TracedDiffuseBuffers(traced.Value, filtered.Value, blend.Value));
    }

    /// <summary>
    /// The rays traced from the view's surfaces through the world, lit by the lights, the sky and the probes, then
    /// filtered and blended with the history.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes, in TracedDiffuseBuffers diffuse)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        graph
            .Compute("Trace diffuse", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Frame, view.Constants, view.Depth, view.GBufferNormal)
            .Reads(probes.Probes, probes.Depth)
            .Writes(diffuse.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Filter diffuse", _filterShader, header, RenderStage.Light)
            .Reads(view.Frame, view.Constants, view.Depth, diffuse.Traced)
            .Writes(diffuse.Filtered)
            .DispatchOver(width, height);
        graph
            .Compute("Blend diffuse", _blendShader, header, RenderStage.Light)
            .Reads(
                view.Frame,
                view.Constants,
                view.Depth,
                view.Velocity,
                diffuse.Filtered,
                diffuse.Blend.LightHistory,
                diffuse.Blend.DepthHistory
            )
            .Writes(diffuse.Blend.Light, diffuse.Blend.Depth)
            .DispatchOver(width, height);
    }
}

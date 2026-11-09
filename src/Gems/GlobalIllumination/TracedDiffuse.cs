using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// A frame's traced diffuse light, at half the view's size: the rays' light, filtered, and blended with the history,
/// with the view depth of each blended pixel; the history is what the last frame blended, and the constants name them.
/// </summary>
internal readonly record struct TracedDiffuseBuffers(
    TracedDiffuseConstants Constants,
    GpuTexture Traced,
    GpuTexture Filtered,
    GpuTexture Light,
    GpuTexture Depth,
    GpuTexture LightHistory,
    GpuTexture DepthHistory
);

/// <summary>
/// On a GPU that traces rays, the light that reaches each surface indirectly, traced near it: two rays from a pixel of
/// each 2 by 2, lit where they land by the lights and the probes, and the probes' light where they reach nothing within
/// range; filtered over neighbours and blended over frames, into the light shading reads. It blends with last frame's
/// light and depths, so it keeps two of each and swaps them every frame, and with none while the view has no history.
/// </summary>
internal sealed class TracedDiffuse(AssetManager assets)
{
    private static readonly string[] _depthNames = ["Diffuse depth 0", "Diffuse depth 1"];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(assets, "TraceDiffuse", "TraceDiffuse") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _filterShader = PassShaders.GetEntry(assets, "TraceDiffuse", "FilterDiffuse");
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceDiffuse", "BlendDiffuse");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _filterShader, _blendShader];

    /// <summary>
    /// The targets asked for at half the view's size, the even frames writing the first light and depth and the odd
    /// the second, the other being the history, and the constants that name them; the light is the indirect diffuse
    /// light shading reads.
    /// </summary>
    public static Result<TracedDiffuseBuffers> Prepare(in GIView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = view.TracedSize;
        int written = (int)(frameIndex % 2);
        Result<GpuTexture> traced = graph.Texture(GetTarget("Traced diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> filtered = graph.Texture(GetTarget("Filtered diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> light = graph.Texture(GetLight(written));
        Result<GpuTexture> depth = graph.Texture(GetTarget(_depthNames[written], GpuFormat.R32Sfloat));
        Result<GpuTexture> lightHistory = graph.Texture(GetLight(1 - written));
        Result<GpuTexture> depthHistory = graph.Texture(GetTarget(_depthNames[1 - written], GpuFormat.R32Sfloat));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)[traced, filtered, light, depth, lightHistory, depthHistory, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        TracedDiffuseConstants constants = new()
        {
            Traced = traced.Value.Id,
            Filtered = filtered.Value.Id,
            Light = light.Value.Id,
            Depth = depth.Value.Id,
            LightHistory = lightHistory.Value.Id,
            DepthHistory = depthHistory.Value.Id,
            Sampler = sampler.Value.Id,
            HasHistory = view.HasHistory ? 1u : 0u,
        };
        TracedDiffuseBuffers buffers = new(
            constants,
            traced.Value,
            filtered.Value,
            light.Value,
            depth.Value,
            lightHistory.Value,
            depthHistory.Value
        );

        return Result.Success(buffers);

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);

        TextureDescription GetLight(int frame) =>
            SharedResources.IndirectDiffuse[frame] with
            {
                Width = width,
                Height = height,
            };
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
            .Reads(view.Frame, view.Constants, view.IndirectLight, view.Depth, view.GBufferNormal)
            .Reads(probes.Probes, probes.Depth)
            .Writes(diffuse.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Filter diffuse", _filterShader, header, RenderStage.Light)
            .Reads(view.Frame, view.Constants, view.IndirectLight, view.Depth, diffuse.Traced)
            .Writes(diffuse.Filtered)
            .DispatchOver(width, height);
        graph
            .Compute("Blend diffuse", _blendShader, header, RenderStage.Light)
            .Reads(
                view.Frame,
                view.Constants,
                view.IndirectLight,
                view.Depth,
                view.Velocity,
                diffuse.Filtered,
                diffuse.LightHistory,
                diffuse.DepthHistory
            )
            .Writes(diffuse.Light, diffuse.Depth)
            .DispatchOver(width, height);
    }
}

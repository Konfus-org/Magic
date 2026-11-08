using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A frame's traced diffuse light, at half the view's size: the rays' light, filtered, and blended with the history,
/// with the view depth of each blended pixel; the history is what the last frame blended, and the constants name them.
/// </summary>
internal readonly record struct TracedDiffuseBuffers(
    GpuBuffer Constants,
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
internal sealed class TracedDiffuse(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// The twin of <c>Scene/TracedDiffuse.slang</c>'s.
    /// </summary>
    private const int TracedScale = 2;

    private static readonly string[] _lightNames = ["Diffuse light 0", "Diffuse light 1"];
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
    /// the second, the other being the history, and the constants uploaded, in a transfer pass the caller opened.
    /// </summary>
    public static Result<TracedDiffuseBuffers> Prepare(in RenderView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = GetSize(view);
        int written = (int)(frameIndex % 2);
        Result<GpuBuffer> constants = graph.Buffer(
            GpuDescriptions.GetBuffer("Traced diffuse constants", TracedDiffuseConstants.Bytes)
        );
        Result<GpuTexture> traced = graph.Texture(GetTarget("Traced diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> filtered = graph.Texture(GetTarget("Filtered diffuse", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> light = graph.Texture(GetTarget(_lightNames[written], GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> depth = graph.Texture(GetTarget(_depthNames[written], GpuFormat.R32Sfloat));
        Result<GpuTexture> lightHistory = graph.Texture(
            GetTarget(_lightNames[1 - written], GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuTexture> depthHistory = graph.Texture(GetTarget(_depthNames[1 - written], GpuFormat.R32Sfloat));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (
            Result made in (ReadOnlySpan<Result>)
                [constants, traced, filtered, light, depth, lightHistory, depthHistory, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        TracedDiffuseBuffers buffers = new(
            constants.Value,
            traced.Value,
            filtered.Value,
            light.Value,
            depth.Value,
            lightHistory.Value,
            depthHistory.Value
        );
        TracedDiffuseConstants uploaded = new()
        {
            Traced = buffers.Traced.Id,
            Filtered = buffers.Filtered.Id,
            Light = buffers.Light.Id,
            Depth = buffers.Depth.Id,
            LightHistory = buffers.LightHistory.Id,
            DepthHistory = buffers.DepthHistory.Id,
            Sampler = sampler.Value.Id,
            HasHistory = view.HasHistory ? 1u : 0u,
        };
        graph.Upload(buffers.Constants, [uploaded]);

        return Result.Success(buffers);

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);
    }

    /// <summary>
    /// The rays traced from the view's surfaces through the world, lit by the lights, the sky and the probes, then
    /// filtered and blended with the history.
    /// </summary>
    public void Record(
        in RenderView view,
        GpuBuffer lightingConstants,
        GpuBuffer hitMaterials,
        in SkyTables sky,
        in LightBuffers lights,
        in ProbeBuffers probes,
        in TracedDiffuseBuffers diffuse
    )
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = GetSize(view);
        GpuConstants header = new(view.Constants.Id, probes.Constants.Id, lightingConstants.Id, diffuse.Constants.Id);
        graph
            .Compute("Trace diffuse", _traceShader, header)
            .Reads(
                view.Constants,
                lightingConstants,
                diffuse.Constants,
                view.Depth,
                view.GBufferNormal,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                lights.Table,
                view.Instances,
                resources.ClusterPool,
                resources.VertexPool,
                resources.IndexPool,
                resources.LeafClusters,
                hitMaterials,
                probes.Constants,
                probes.Probes,
                probes.Depth
            )
            .Writes(diffuse.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Filter diffuse", _filterShader, header)
            .Reads(view.Constants, diffuse.Constants, view.Depth, diffuse.Traced)
            .Writes(diffuse.Filtered)
            .DispatchOver(width, height);
        graph
            .Compute("Blend diffuse", _blendShader, header)
            .Reads(
                view.Constants,
                diffuse.Constants,
                view.Depth,
                view.Velocity,
                probes.Constants,
                diffuse.Filtered,
                diffuse.LightHistory,
                diffuse.DepthHistory
            )
            .Writes(diffuse.Light, diffuse.Depth)
            .DispatchOver(width, height);
    }

    private static (int Width, int Height) GetSize(in RenderView view) =>
        (Math.Max(view.Width / TracedScale, 1), Math.Max(view.Height / TracedScale, 1));
}

using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A frame's traced reflections, at half the view's size: the rays' light and their directions, resolved over the
/// neighbours with the farthest ray that counts, and blended with the history, with the view depth of each blended
/// pixel; the history is what the last frame blended, and the constants name them.
/// </summary>
internal readonly record struct TracedReflectionBuffers(
    TracedReflectionConstants Constants,
    GpuTexture Traced,
    GpuTexture Rays,
    GpuTexture Resolved,
    GpuTexture Distance,
    GpuTexture Light,
    GpuTexture Depth,
    GpuTexture LightHistory,
    GpuTexture DepthHistory
);

/// <summary>
/// On a GPU that traces rays, what each smooth surface reflects: a ray from a pixel of each 2 by 2 along a direction
/// its specular reflects the view into, lit where it lands by a light and the probes, or the sky where it lands
/// nowhere; resolved over neighbours and blended over frames, into the light shading reads. It blends with last
/// frame's light and depths, so it keeps two of each and swaps them every frame, and with none while the view has no
/// history.
/// </summary>
internal sealed class TracedReflections(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// The twin of <c>Scene/Traced.slang</c>'s.
    /// </summary>
    private const int TracedScale = 2;

    private static readonly string[] _lightNames = ["Reflection light 0", "Reflection light 1"];
    private static readonly string[] _depthNames = ["Reflection depth 0", "Reflection depth 1"];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(
        assets,
        "TraceReflections",
        "TraceReflections"
    ) with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _resolveShader = PassShaders.GetEntry(
        assets,
        "TraceReflections",
        "ResolveReflections"
    );
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceReflections", "BlendReflections");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _resolveShader, _blendShader];

    /// <summary>
    /// The targets asked for at half the view's size, the even frames writing the first light and depth and the odd
    /// the second, the other being the history, and the constants that name them.
    /// </summary>
    public static Result<TracedReflectionBuffers> Prepare(in RenderView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = GetSize(view);
        int written = (int)(frameIndex % 2);
        Result<GpuTexture> traced = graph.Texture(GetTarget("Traced reflections", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> rays = graph.Texture(GetTarget("Reflection rays", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> resolved = graph.Texture(GetTarget("Resolved reflections", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> distance = graph.Texture(GetTarget("Reflection distance", GpuFormat.R32Sfloat));
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
                [traced, rays, resolved, distance, light, depth, lightHistory, depthHistory, sampler]
        )
        {
            if (!made.Ok)
                return Result.Failure(made.Error);
        }

        TracedReflectionConstants constants = new()
        {
            Traced = traced.Value.Id,
            Rays = rays.Value.Id,
            Resolved = resolved.Value.Id,
            Distance = distance.Value.Id,
            Light = light.Value.Id,
            Depth = depth.Value.Id,
            LightHistory = lightHistory.Value.Id,
            DepthHistory = depthHistory.Value.Id,
            Sampler = sampler.Value.Id,
            HasHistory = view.HasHistory ? 1u : 0u,
        };
        TracedReflectionBuffers buffers = new(
            constants,
            traced.Value,
            rays.Value,
            resolved.Value,
            distance.Value,
            light.Value,
            depth.Value,
            lightHistory.Value,
            depthHistory.Value
        );

        return Result.Success(buffers);

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);
    }

    /// <summary>
    /// The rays traced from the view's smooth surfaces through the world, lit by the lights, the sky and the probes,
    /// then resolved and blended with the history.
    /// </summary>
    public void Record(
        in RenderView view,
        GpuBuffer hitMaterials,
        in SkyTables sky,
        in LightBuffers lights,
        in ProbeBuffers probes,
        in TracedReflectionBuffers reflections
    )
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = GetSize(view);
        GpuConstants header = new(view.Constants.Id);
        graph
            .Compute("Trace reflections", _traceShader, header)
            .Reads(
                view.Constants,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                lights.Table,
                view.Instances,
                resources.VertexPool,
                resources.IndexPool,
                resources.Meshes,
                hitMaterials,
                probes.Probes,
                probes.Depth
            )
            .Writes(reflections.Traced, reflections.Rays)
            .DispatchOver(width, height);
        graph
            .Compute("Resolve reflections", _resolveShader, header)
            .Reads(
                view.Constants,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                reflections.Traced,
                reflections.Rays
            )
            .Writes(reflections.Resolved, reflections.Distance)
            .DispatchOver(width, height);
        graph
            .Compute("Blend reflections", _blendShader, header)
            .Reads(
                view.Constants,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface,
                view.Velocity,
                reflections.Resolved,
                reflections.Distance,
                reflections.LightHistory,
                reflections.DepthHistory
            )
            .Writes(reflections.Light, reflections.Depth)
            .DispatchOver(width, height);
    }

    private static (int Width, int Height) GetSize(in RenderView view) =>
        (Math.Max(view.Width / TracedScale, 1), Math.Max(view.Height / TracedScale, 1));
}

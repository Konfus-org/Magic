using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// On a GPU that traces rays, what each smooth surface reflects: a ray from a pixel of each traced pixel's footprint
/// along a direction its specular reflects the view into, lit where it lands by a light and the probes, or the sky
/// where it lands nowhere; resolved over neighbours and blended over frames, into the light shading reads.
/// </summary>
internal sealed class TracedReflections(AssetManager assets)
{
    private static readonly string[] _blendNames =
    [
        "Reflection light 0",
        "Reflection light 1",
        "Reflection depth 0",
        "Reflection depth 1",
    ];

    private readonly ShaderEntry _traceShader = PassShaders.GetEntry(
        assets,
        "TraceReflections",
        "TraceReflections"
    ) with
    {
        TypeArguments = [GpuView.GetAtmosphere(assets)],
    };
    private readonly ShaderEntry _resolveShader = PassShaders.GetEntry(
        assets,
        "TraceReflections",
        "ResolveReflections"
    );
    private readonly ShaderEntry _blendShader = PassShaders.GetEntry(assets, "TraceReflections", "BlendReflections");

    public ImmutableArray<ShaderEntry> Shaders => [_traceShader, _resolveShader, _blendShader];

    /// <summary>
    /// The textures asked for at the traced size; the blended light is the indirect specular light shading reads.
    /// </summary>
    public static Result<TracedReflectionConstants> Prepare(in GIView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = graph.Texture(view.GetTraced("Traced reflections", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> rays = graph.Texture(view.GetTraced("Reflection rays", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> resolved = graph.Texture(
            view.GetTraced("Resolved reflections", GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuTexture> distance = graph.Texture(view.GetTraced("Reflection distance", GpuFormat.R32Sfloat));
        Result<TracedBlendConstants> blend = TracedBlend.Prepare(view, _blendNames, frameIndex);
        if (Result.FindFailure(traced, rays, resolved, distance, blend) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new TracedReflectionConstants
            {
                Traced = traced.Value,
                Rays = rays.Value,
                Resolved = resolved.Value,
                Distance = distance.Value,
                Blend = blend.Value,
            }
        );
    }

    /// <summary>
    /// The rays traced from the view's smooth surfaces through the world, lit by the lights, the sky and the probes,
    /// then resolved and blended with the history.
    /// </summary>
    public void Record(in GIView view, in ProbeBuffers probes, in TracedReflectionConstants reflections)
    {
        RenderGraph graph = view.Graph;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        graph
            .Compute("Trace reflections", _traceShader, header, RenderStage.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal, view.Gpu.GBufferSurface)
            .Reads(probes.Probes, probes.Depth)
            .Writes(reflections.Traced, reflections.Rays)
            .DispatchOver(width, height);
        graph
            .Compute("Resolve reflections", _resolveShader, header, RenderStage.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferSurface,
                reflections.Traced,
                reflections.Rays
            )
            .Writes(reflections.Resolved, reflections.Distance)
            .DispatchOver(width, height);
        graph
            .Compute("Blend reflections", _blendShader, header, RenderStage.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferSurface,
                view.Gpu.Velocity,
                reflections.Resolved,
                reflections.Distance,
                reflections.Blend.LightHistory,
                reflections.Blend.DepthHistory
            )
            .Writes(reflections.Blend.Light, reflections.Blend.Depth)
            .DispatchOver(width, height);
    }
}

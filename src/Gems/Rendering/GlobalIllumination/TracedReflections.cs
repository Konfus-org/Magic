using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// Traces what each smooth surface reflects, on a GPU that traces rays.
/// </summary>
/// <remarks>
/// One ray goes from a pixel of each traced pixel's footprint, along a direction that its specular reflects the view
/// into. A light and the probes light it where it lands, and the sky where it lands nowhere. The light is resolved over
/// neighbours and blended over frames into the light that shading reads.
/// </remarks>
internal sealed class TracedReflections(AssetManager assets)
{
    private static readonly string[] _blendNames = ["Reflection light 0", "Reflection light 1"];

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
    /// Asks for the textures at the traced size, the blend's history holding the view as
    /// <paramref name="hasHistory"/> says.
    /// </summary>
    /// <remarks>
    /// The blended light is the indirect specular light that shading reads.
    /// </remarks>
    public static Result<TracedReflectionConstants> Prepare(in GIView view, uint frameIndex, bool hasHistory)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = graph.Texture(view.GetTraced("Traced reflections", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> rays = graph.Texture(view.GetTraced("Reflection rays", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> resolved = graph.Texture(
            view.GetTraced("Resolved reflections", GpuFormat.R16G16B16A16Sfloat)
        );
        Result<GpuTexture> distance = graph.Texture(view.GetTraced("Reflection distance", GpuFormat.R32Sfloat));
        Result<TracedBlendConstants> blend = TracedBlend.Prepare(view, _blendNames, frameIndex, hasHistory);
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
    /// Traces rays from the view's smooth surfaces through the world, then resolves them and blends them with the
    /// history.
    /// </summary>
    /// <remarks>
    /// The lights, the sky and the probes light the rays. The blend reads where the diffuse blend kept last frame's
    /// view depth.
    /// </remarks>
    public void Record(in GIView view, in ProbeGridConstants grid, in GIConstants gi)
    {
        RenderGraph graph = view.Graph;
        TracedReflectionConstants reflections = gi.Reflections;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        graph
            .Compute("Trace reflections", _traceShader, header, RenderStages.Light)
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal, view.Gpu.GBufferSurface)
            .Reads(grid.Probes, grid.Depth)
            .Writes(reflections.Traced, reflections.Rays)
            .DispatchOver(width, height);
        graph
            .Compute("Resolve reflections", _resolveShader, header, RenderStages.Light)
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
            .Compute("Blend reflections", _blendShader, header, RenderStages.Light)
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
                gi.DepthHistory
            )
            .Writes(reflections.Blend.Light)
            .DispatchOver(width, height);
    }
}

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
/// A few rays go from a pixel of each traced pixel's footprint. The lights and the probes light them where they land, and
/// the probes' light is used where they reach nothing within range. A hit that the screen showed last frame takes the
/// light of the screen there in place of the probes'. The trace writes NRD's inputs, which the
/// <see cref="Denoiser"/> turns into the light that shading reads.
/// </remarks>
internal sealed class TracedDiffuse(AssetManager assets)
{
    private readonly ShaderEntry _skyShareShader = PassShaders.GetEntry(assets, "TraceDiffuse", "BlendSkyShare");

    /// <summary>
    /// The trace's entry, generic over the sky, which the renderer specialises on the frame's sky.
    /// </summary>
    internal ShaderEntry TraceShader { get; } = PassShaders.GetEntry(assets, "TraceDiffuse", "TraceDiffuse");

    internal ImmutableArray<ShaderEntry> Shaders => [_skyShareShader];

    /// <summary>
    /// Asks for the trace's texture at the traced size, beside NRD's inputs.
    /// </summary>
    internal static Result<TracedDiffuseConstants> Prepare(
        IGraphicsApi gpu,
        in GIView view,
        in DenoiserTextures nrd,
        bool hasHistory
    )
    {
        Result<GpuTexture> traced = gpu.GetTexture(
            view.GetTraced("Traced diffuse", GpuFormat.R16G16B16A16Sfloat),
            view.Index
        );
        if (!traced.Ok)
            return Result.Failure(traced.Error);

        return Result.Success(
            new TracedDiffuseConstants
            {
                Traced = traced.Value,
                Radiance = nrd.Radiance,
                Motion = nrd.Motion,
                NormalRoughness = nrd.NormalRoughness,
                ViewZ = nrd.ViewZ,
                HasHistory = hasHistory ? 1u : 0u,
            }
        );
    }

    /// <summary>
    /// Records the trace, which lights a hit on the screen with <paramref name="lastLight"/>, last frame's denoised
    /// light, before this frame's denoise writes it again.
    /// </summary>
    internal void RecordTrace(in GIView view, in ProbeGridConstants grid, in GIConstants gi, GpuTexture lastLight)
    {
        TracedDiffuseConstants diffuse = gi.Diffuse;
        (int width, int height) = view.TracedSize;
        view.Graph.Compute(
                "Trace diffuse",
                TraceShader with
                {
                    TypeArguments = view.SkyType,
                },
                view.Header,
                RenderStages.Light
            )
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal, view.Gpu.Velocity)
            .Reads(grid.Probes, grid.Depth, lastLight, gi.DepthHistory)
            .Writes(diffuse.Traced, diffuse.Radiance, diffuse.Motion, diffuse.NormalRoughness, diffuse.ViewZ)
            .DispatchOver(width, height);
    }

    internal void RecordSkyShare(in GIView view, in GIConstants gi)
    {
        (int width, int height) = view.TracedSize;
        view.Graph.Compute("Blend sky share", _skyShareShader, view.Header, RenderStages.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.Velocity,
                gi.Diffuse.Traced,
                gi.DepthHistory
            )
            .Writes(gi.Depth)
            .DispatchOver(width, height);
    }
}

using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.Deferred;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was: the frame's shadows, naming the
/// shadow mask, uploaded before the view is drawn, and the shading after them, lit indirectly by the frame's indirect light where there is any; then the nearest translucent surface
/// shaded over it.
/// </summary>
internal sealed class Lighting(AssetManager assets)
{
    private readonly ShaderEntry _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    private readonly ShaderEntry _translucentShader = PassShaders.GetEntry(
        assets,
        "ShadeTranslucent",
        "ShadeTranslucent"
    ) with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    public ImmutableArray<ShaderEntry> Shaders => [_shadeShader, _translucentShader];

    /// <summary>
    /// The frame's shadows uploaded into the frame's buffer, after the lights' row: the mask's ids, all 0 where nothing
    /// traces shadows.
    /// </summary>
    public static void UploadShadows(in RenderView view, ShadowBuffers? shadows)
    {
        GpuShadows frameShadows = new()
        {
            Mask = shadows?.Mask.Id ?? 0,
            Traced = shadows?.Traced.Id ?? 0,
            SunTiles = shadows?.SunTiles.Id ?? 0,
            SunTint = shadows?.SunTint.Id ?? 0,
        };
        view.Graph.Upload(view.Gpu.Frame, [frameShadows], GpuShadows.Offset);
    }

    /// <summary>
    /// The frame's indirect light, its buffer and, on a GPU that traces rays, what it can name, asked for by Core's
    /// descriptions at the view's size, so shading reads them after whatever traced them wrote them; they hold nothing
    /// it reads while nothing traces them.
    /// </summary>
    public static Result<ImmutableArray<GpuResource>> GetIndirect(in RenderView view)
    {
        RenderGraph graph = view.Graph;
        if (!graph.Features.HasRayQuery)
            return Result.Success<ImmutableArray<GpuResource>>([view.Gpu.IndirectLight]);

        (int width, int height) = (Math.Max(view.Gpu.Width / 2, 1), Math.Max(view.Gpu.Height / 2, 1));
        ImmutableArray<GpuResource>.Builder indirect = ImmutableArray.CreateBuilder<GpuResource>();
        indirect.Add(view.Gpu.IndirectLight);
        foreach (
            TextureDescription light in (ReadOnlySpan<TextureDescription>)
                [.. SharedResources.IndirectDiffuse, .. SharedResources.IndirectSpecular]
        )
        {
            Result<GpuTexture> texture = graph.Texture(light with { Width = width, Height = height });
            if (!texture.Ok)
                return Result.Failure(texture.Error);

            indirect.Add(texture.Value);
        }

        Result<GpuBuffer> probes = graph.Buffer(SharedResources.Probes);
        Result<GpuTexture> probeDepth = graph.Texture(SharedResources.ProbeDepth);
        if (!probes.Ok || !probeDepth.Ok)
            return Result.Failure(probes.Ok ? probeDepth.Error : probes.Error);

        indirect.Add(probes.Value);
        indirect.Add(probeDepth.Value);

        return Result.Success(indirect.ToImmutable());
    }

    /// <summary>
    /// Every pixel's surface shaded from the sky's tables, its tile's lights and, on a GPU that traces rays, its
    /// shadows, and from <paramref name="indirect"/>, or the sky where it was not written for the frame; then the
    /// nearest glass over it, lit by the light around it from the probes where there are any, and its rays through the
    /// world on a GPU that traces them.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        ShadowBuffers? shadows,
        ImmutableArray<GpuResource> indirect
    )
    {
        ComputePass shade = view
            .Graph.Compute("Shade surfaces", _shadeShader, header, RenderStage.Shade)
            .Reads(
                view.Gpu.Frame,
                view.Gpu.SkyTransmittance,
                view.Gpu.SkyMultiScattering,
                view.Gpu.SkyView,
                view.Gpu.LightTable,
                view.Gpu.LightTiles,
                view.Visibility,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferBase,
                view.Gpu.GBufferSurface
            );
        if (shadows is { } shadowed)
            shade.Reads(shadowed.Mask, shadowed.SunTint);

        shade.Reads(indirect.AsSpan());

        shade.Writes(view.Gpu.Hdr).DispatchOver(view.Gpu.Width, view.Gpu.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets.
        ComputePass translucent = view
            .Graph.Compute("Shade translucent", _translucentShader, header, RenderStage.Shade)
            .Reads(
                view.Gpu.Frame,
                view.Gpu.SkyTransmittance,
                view.Gpu.SkyMultiScattering,
                view.Gpu.SkyView,
                view.Gpu.GlassDepth,
                view.Gpu.GlassBase,
                view.Gpu.GlassNormal,
                view.Gpu.GlassSurface,
                view.Gpu.Depth
            );
        translucent.Reads(indirect.AsSpan());

        if (!view.Gpu.HitMaterials.IsNone)
            translucent.Reads(view.Gpu.HitMaterials, view.Gpu.Meshes, view.Gpu.Instances);

        translucent.Writes(view.Gpu.Hdr).DispatchOver(view.Gpu.Width, view.Gpu.Height);
    }
}

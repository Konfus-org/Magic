using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The sky's three tables: how much of the sun gets through the air, what the air scatters back into itself, and the
/// sky as the camera sees it.
/// </summary>
internal readonly record struct SkyTables(GpuTexture Transmittance, GpuTexture MultiScattering, GpuTexture View);

/// <summary>
/// The passes that make the sky's tables, each frame while there is a sky, their shaders built by the GPU provider.
/// </summary>
internal sealed class SkyLuts(AssetManager assets)
{
    private readonly ShaderEntry _transmittanceShader = PassShaders.GetEntry(assets, "SkyLuts", "SkyTransmittance");
    private readonly ShaderEntry _multiScatteringShader = PassShaders.GetEntry(assets, "SkyLuts", "SkyMultiScattering");
    private readonly ShaderEntry _viewShader = PassShaders.GetEntry(assets, "SkyLuts", "SkyView");

    public ImmutableArray<ShaderEntry> Shaders => [_transmittanceShader, _multiScatteringShader, _viewShader];

    /// <summary>
    /// The tables asked for, before the lights name them.
    /// </summary>
    public static Result<SkyTables> Prepare(RenderGraph graph)
    {
        Result<GpuTexture> transmittance = graph.Texture(SharedResources.SkyTransmittance);
        Result<GpuTexture> multiScattering = graph.Texture(SharedResources.SkyMultiScattering);
        Result<GpuTexture> view = graph.Texture(SharedResources.SkyView);
        foreach (Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)[transmittance, multiScattering, view])
        {
            if (!texture.Ok)
                return Result.Failure(texture.Error);
        }

        return Result.Success(new SkyTables(transmittance.Value, multiScattering.Value, view.Value));
    }

    public void Record(RenderGraph graph, in GpuConstants header, GpuBuffer frameConstants, in SkyTables tables)
    {
        graph
            .Compute("Sky: transmittance", _transmittanceShader, header, RenderStage.Prepare)
            .Reads(frameConstants)
            .Writes(tables.Transmittance)
            .DispatchOver(256, 64);
        graph
            .Compute("Sky: multi-scattering", _multiScatteringShader, header, RenderStage.Prepare)
            .Reads(frameConstants, tables.Transmittance)
            .Writes(tables.MultiScattering)
            .DispatchOver(32, 32);
        graph
            .Compute("Sky: view", _viewShader, header, RenderStage.Prepare)
            .Reads(frameConstants, tables.Transmittance, tables.MultiScattering)
            .Writes(tables.View)
            .DispatchOver(192, 108);
    }
}

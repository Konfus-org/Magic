using System.Collections.Immutable;
using Magic.Assets;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
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
    /// The tables asked for, before the lighting constants name them.
    /// </summary>
    public static Result<SkyTables> Prepare(RenderGraph graph)
    {
        Result<GpuTexture> transmittance = graph.Texture(GetTable("Sky transmittance", 256, 64));
        Result<GpuTexture> multiScattering = graph.Texture(GetTable("Sky multi-scattering", 32, 32));
        Result<GpuTexture> view = graph.Texture(GetTable("Sky view", 192, 108));
        foreach (Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)[transmittance, multiScattering, view])
        {
            if (!texture.Ok)
                return Result.Failure(texture.Error);
        }

        return Result.Success(new SkyTables(transmittance.Value, multiScattering.Value, view.Value));
    }

    public void Record(
        RenderGraph graph,
        in GpuConstants header,
        GpuBuffer frameConstants,
        GpuBuffer lightingConstants,
        in SkyTables tables
    )
    {
        graph
            .Compute("Sky: transmittance", _transmittanceShader, header)
            .Reads(lightingConstants)
            .Writes(tables.Transmittance)
            .DispatchOver(256, 64);
        graph
            .Compute("Sky: multi-scattering", _multiScatteringShader, header)
            .Reads(lightingConstants, tables.Transmittance)
            .Writes(tables.MultiScattering)
            .DispatchOver(32, 32);
        graph
            .Compute("Sky: view", _viewShader, header)
            .Reads(frameConstants, lightingConstants, tables.Transmittance, tables.MultiScattering)
            .Writes(tables.View)
            .DispatchOver(192, 108);
    }

    private static TextureDescription GetTable(string name, int width, int height) =>
        GpuDescriptions.GetTexture(name, width, height, GpuFormat.R16G16B16A16Sfloat);
}

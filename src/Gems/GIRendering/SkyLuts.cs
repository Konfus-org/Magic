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
    private const string Module = "Shaders/SkyLuts.slang";

    private readonly ShaderEntry _transmittanceShader = GetEntry(assets, "SkyTransmittance");
    private readonly ShaderEntry _multiScatteringShader = GetEntry(assets, "SkyMultiScattering");
    private readonly ShaderEntry _viewShader = GetEntry(assets, "SkyView");

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

    /// <summary>
    /// Where the three passes' shaders are: failed if any failed, loading while any loads, else ready. Each is asked,
    /// so the first ask starts all three.
    /// </summary>
    public GpuState GetState(RenderGraph graph)
    {
        ReadOnlySpan<GpuState> states =
        [
            graph.GetState(_transmittanceShader),
            graph.GetState(_multiScatteringShader),
            graph.GetState(_viewShader),
        ];
        if (states.Contains(GpuState.Failed))
            return GpuState.Failed;

        return states.Contains(GpuState.Loading) ? GpuState.Loading : GpuState.Ready;
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

    private static ShaderEntry GetEntry(AssetManager assets, string name) =>
        new()
        {
            Module = assets.Find<Shader>(Module),
            Name = name,
            Stage = ShaderStage.Compute,
        };

    private static TextureDescription GetTable(string name, int width, int height) =>
        new()
        {
            Width = width,
            Height = height,
            Format = GpuFormat.R16G16B16A16Sfloat,
            Usage = TextureUsages.Storage | TextureUsages.Sampled,
            Name = name,
        };
}

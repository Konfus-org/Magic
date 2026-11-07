using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The sky's three tables: how much of the sun gets through the air, what the air scatters back into itself, and the
/// sky as the camera sees it.
/// </summary>
internal readonly record struct SkyTables(GpuTexture Transmittance, GpuTexture MultiScattering, GpuTexture View);

/// <summary>
/// The passes that make the sky's tables, each frame while there is a sky.
/// </summary>
internal sealed class SkyLuts(GpuResources resources, GpuPipelines pipelines)
{
    private readonly GpuPass _transmittancePass = pipelines.Compute(
        "Sky: transmittance",
        "SkyLuts",
        "SkyTransmittance"
    );
    private readonly GpuPass _multiScatteringPass = pipelines.Compute(
        "Sky: multi-scattering",
        "SkyLuts",
        "SkyMultiScattering"
    );
    private readonly GpuPass _viewPass = pipelines.Compute("Sky: view", "SkyLuts", "SkyView");

    /// <summary>
    /// The tables asked for, before the lighting constants name them.
    /// </summary>
    public Result<SkyTables> Prepare()
    {
        Result<GpuTexture> transmittance = resources.TryGetTexture(
            "Sky transmittance",
            256,
            64,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> multiScattering = resources.TryGetTexture(
            "Sky multi-scattering",
            32,
            32,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> view = resources.TryGetTexture("Sky view", 192, 108, GpuFormat.R16G16B16A16Sfloat);
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
        _transmittancePass
            .Run(graph, header)
            .Reads(lightingConstants)
            .Writes(tables.Transmittance)
            .DispatchOver(256, 64);
        _multiScatteringPass
            .Run(graph, header)
            .Reads(lightingConstants, tables.Transmittance)
            .Writes(tables.MultiScattering)
            .DispatchOver(32, 32);
        _viewPass
            .Run(graph, header)
            .Reads(frameConstants, lightingConstants, tables.Transmittance, tables.MultiScattering)
            .Writes(tables.View)
            .DispatchOver(192, 108);
    }
}

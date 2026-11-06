using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The sky's three tables, made each frame while there is a sky: how much of the sun gets through the air, what the air
/// scatters back into itself, and the sky as the camera sees it.
/// </summary>
internal sealed class SkyLuts(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines) : IDisposable
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

    public GpuTexture Transmittance { get; private set; }

    public GpuTexture MultiScattering { get; private set; }

    public GpuTexture View { get; private set; }

    public void Dispose()
    {
        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[Transmittance, MultiScattering, View])
        {
            gpu.Destroy(texture);
        }
    }

    public Result Create()
    {
        Result<GpuTexture> transmittance = gpu.CreateTexture(
            "Sky transmittance",
            256,
            64,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> multiScattering = gpu.CreateTexture(
            "Sky multi-scattering",
            32,
            32,
            GpuFormat.R16G16B16A16Sfloat
        );
        Result<GpuTexture> view = gpu.CreateTexture("Sky view", 192, 108, GpuFormat.R16G16B16A16Sfloat);
        foreach (Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)[transmittance, multiScattering, view])
        {
            if (!texture.Ok)
                return Result.Failure(texture.Error);
        }

        Transmittance = transmittance.Value;
        MultiScattering = multiScattering.Value;
        View = view.Value;

        return Result.Success();
    }

    /// <summary>
    /// Says a pass reads the tables.
    /// </summary>
    public void UseForReading()
    {
        graph.Use(Transmittance, GpuAccess.ShaderRead);
        graph.Use(MultiScattering, GpuAccess.ShaderRead);
        graph.Use(View, GpuAccess.ShaderRead);
    }

    public void Record(in GpuConstants header, GpuBuffer frameConstants, GpuBuffer lightingConstants)
    {
        _transmittancePass.Run(header).Reads(lightingConstants).Writes(Transmittance).DispatchOver(256, 64);
        _multiScatteringPass
            .Run(header)
            .Reads(lightingConstants, Transmittance)
            .Writes(MultiScattering)
            .DispatchOver(32, 32);
        _viewPass
            .Run(header)
            .Reads(frameConstants, lightingConstants, Transmittance, MultiScattering)
            .Writes(View)
            .DispatchOver(192, 108);
    }
}

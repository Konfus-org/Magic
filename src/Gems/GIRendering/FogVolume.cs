using Magic.Gpu;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The air: a volume over the view that the sun, through its pages, and the lights scatter towards the camera, summed
/// from the camera out, so a pixel reads what the air in front of it adds and keeps.
/// </summary>
internal sealed class FogVolume(
    IGraphicsApi gpu,
    GpuPipelines pipelines,
    SkyLuts sky,
    LightBins bins,
    SunShadows shadows
) : IDisposable
{
    private const int GridX = 96;
    private const int GridY = 54;
    private const int GridZ = 64;
    private const int InjectThreads = 4;

    private readonly GpuPass _injectPass = pipelines.Compute(
        "Fog: inject",
        "FogVolume",
        "FogInject",
        PassShaders.Atmosphere
    );
    private readonly GpuPass _integratePass = pipelines.Compute("Fog: integrate", "FogVolume", "FogIntegrate");

    public GpuTexture Scattering { get; private set; }

    public GpuTexture Integrated { get; private set; }

    public void Dispose()
    {
        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[Scattering, Integrated])
        {
            if (!texture.IsNone)
                gpu.Destroy(texture);
        }
    }

    public Result Create()
    {
        Result<GpuTexture> scattering = Lighting.CreateTable(gpu, "Fog scattering", GridX, GridY, GridZ);
        Result<GpuTexture> integrated = Lighting.CreateTable(gpu, "Fog integrated", GridX, GridY, GridZ);
        if (!scattering.Ok || !integrated.Ok)
            return Result.Failure($"a fog table was not made: {scattering.Error}{integrated.Error}");

        Scattering = scattering.Value;
        Integrated = integrated.Value;

        return Result.Success();
    }

    public void Record(in GpuConstants header, GpuBuffer frameConstants, GpuBuffer lightingConstants)
    {
        GpuPassRun injectRun = _injectPass
            .Run(header)
            .Reads(frameConstants, lightingConstants, bins.Table, bins.FroxelLists)
            .Writes(Scattering);
        sky.UseForReading();
        shadows.UseForSampling();
        injectRun.Dispatch(GridX / InjectThreads, (GridY + InjectThreads - 1) / InjectThreads, GridZ / InjectThreads);

        _integratePass
            .Run(header)
            .Reads(lightingConstants, Scattering)
            .Writes(Integrated)
            .Dispatch(GridX / 8, (GridY + 7) / 8);
    }
}

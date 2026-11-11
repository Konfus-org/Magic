using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;

namespace Magic.GpuWorld;

/// <summary>
/// The passes that make the sky's tables, each frame while there is a sky, their shaders built by the GPU provider.
/// </summary>
internal sealed class SkyLuts(AssetManager assets)
{
    private readonly ShaderEntry _transmittanceShader = PassShaders.GetEntry(assets, "SkyLuts", "FillSkyTransmittance");
    private readonly ShaderEntry _multiScatteringShader = PassShaders.GetEntry(
        assets,
        "SkyLuts",
        "FillSkyMultiScattering"
    );
    private readonly ShaderEntry _viewShader = PassShaders.GetEntry(assets, "SkyLuts", "FillSkyView");

    public ImmutableArray<ShaderEntry> Shaders => [_transmittanceShader, _multiScatteringShader, _viewShader];

    public void Record(RenderGraph graph, in GpuView view, in GpuConstants header)
    {
        graph
            .Compute("Sky: transmittance", _transmittanceShader, header, RenderStage.Prepare)
            .Reads(view.Frame)
            .Writes(view.SkyTransmittance)
            .DispatchOver(256, 64);
        graph
            .Compute("Sky: multi-scattering", _multiScatteringShader, header, RenderStage.Prepare)
            .Reads(view.Frame, view.SkyTransmittance)
            .Writes(view.SkyMultiScattering)
            .DispatchOver(32, 32);
        graph
            .Compute("Sky: view", _viewShader, header, RenderStage.Prepare)
            .Reads(view.Frame, view.SkyTransmittance, view.SkyMultiScattering)
            .Writes(view.SkyView)
            .DispatchOver(192, 108);
    }
}

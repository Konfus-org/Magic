using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;

namespace Magic.DeferredRendering.View;

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
        // Each table is filled over its texels, at the size the view's step asked for it.
        TextureDescription transmittance = graph.GetDescription(view.SkyTransmittance);
        TextureDescription multiScattering = graph.GetDescription(view.SkyMultiScattering);
        TextureDescription sky = graph.GetDescription(view.SkyView);
        graph
            .Compute("Sky: transmittance", _transmittanceShader, header, RenderStage.Prepare)
            .Reads(view.Frame)
            .Writes(view.SkyTransmittance)
            .DispatchOver(transmittance.Width, transmittance.Height);
        graph
            .Compute("Sky: multi-scattering", _multiScatteringShader, header, RenderStage.Prepare)
            .Reads(view.Frame, view.SkyTransmittance)
            .Writes(view.SkyMultiScattering)
            .DispatchOver(multiScattering.Width, multiScattering.Height);
        graph
            .Compute("Sky: view", _viewShader, header, RenderStage.Prepare)
            .Reads(view.Frame, view.SkyTransmittance, view.SkyMultiScattering)
            .Writes(view.SkyView)
            .DispatchOver(sky.Width, sky.Height);
    }
}

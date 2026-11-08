using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;

namespace Magic.GIRendering;

/// <summary>
/// The exposure the picture is shown at, metered from a histogram of its luminance and adapted to over time, as an eye
/// adapts. It lives in the view's meter buffer, which every pass that exposes the picture reads; those that run before
/// the meter in a frame read what it metered the frame before.
/// </summary>
internal sealed class ExposureMeter(AssetManager assets)
{
    /// <summary>
    /// The meter buffer's size, the twin of <c>MeterExposure.slang</c>'s layout: the exposure, the luminance adapted to,
    /// the groups that have counted this frame and 1024 bins.
    /// </summary>
    public const int Bytes = (3 + 1024) * sizeof(uint);

    private readonly ShaderEntry _meterShader = PassShaders.GetEntry(assets, "MeterExposure", "MeterExposure");

    public ImmutableArray<ShaderEntry> Shaders => [_meterShader];

    /// <summary>
    /// Starts <paramref name="meter"/> again with no reading, at an exposure of 1 until the first, which it then takes
    /// at once rather than adapting to it.
    /// </summary>
    public static void Restart(RenderGraph graph, GpuBuffer meter)
    {
        Span<float> start = graph.Upload<float>(meter, Bytes / sizeof(float));
        start.Clear();
        start[0] = 1;
    }

    /// <summary>
    /// Meters the picture as it is now, adapting over <paramref name="deltaSec"/>, with
    /// <paramref name="compensationStops"/> added to the exposure it picks.
    /// </summary>
    public void Record(in RenderView view, float deltaSec, float compensationStops) =>
        view
            .Graph.Compute(
                "Meter exposure",
                _meterShader,
                new GpuConstants(
                    view.Constants.Id,
                    BitConverter.SingleToUInt32Bits(deltaSec),
                    BitConverter.SingleToUInt32Bits(compensationStops)
                )
            )
            .Reads(view.Constants, view.Hdr)
            .Writes(view.ExposureMeter)
            .DispatchOver(view.Width / 2, view.Height / 2);
}

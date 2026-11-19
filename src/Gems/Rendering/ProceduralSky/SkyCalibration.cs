using System.Numerics;
using Magic.Graphics;

namespace Magic.ProceduralSky;

/// <summary>
/// The fit of the sky to Blender's multiple scattering sky, which the references render: a gain on each channel of
/// the sky-view table, from the height of the light that lights the world.
/// </summary>
/// <remarks>
/// The gain is <see cref="Gain"/> plus <see cref="GainPerHeight"/> times the Y of the unit direction towards the
/// light, from 0 to 1. The table also takes the light's colour out, as Blender's sky takes only the light's brightest
/// channel. Measured once against Blender 5.3 at the hours 8, 12, 17 and 22, at 30 to 90 degrees over the horizon. The
/// fit is within 2 % of each channel's median there. The table is per unit of the light's illuminance, so the
/// <c>Sky.Brightness</c> of the scene and the light's lux still scale the sky.
/// </remarks>
public static class SkyCalibration
{
    public static readonly Vector3 Gain = new(1.36f, 1.38f, 1.20f);

    public static readonly Vector3 GainPerHeight = new(0.60f, 0.31f, 0.18f);

    /// <summary>
    /// The sky-view pass's header: the frame's buffer, then the gain and its change by height of each channel, as two
    /// half floats in each of Y, Z and W.
    /// </summary>
    public static GpuConstants GetViewHeader(uint frame) =>
        new(frame, Pack(Gain.X, GainPerHeight.X), Pack(Gain.Y, GainPerHeight.Y), Pack(Gain.Z, GainPerHeight.Z));

    private static uint Pack(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16);
}

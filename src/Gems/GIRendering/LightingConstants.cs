using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.GIRendering;

/// <summary>
/// What the lighting passes read beyond the frame, the twin of <c>Scene/Lighting.slang</c>'s: the sun above the air, the
/// viewer's height, the sky's tables, the HDR picture they light, the light table and its tiles, and the shadow mask.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct LightingConstants
{
    public const int Bytes = 80;

    public Vector4 SunIlluminance;

    public Vector4 CameraAltitude;

    public uint TransmittanceLut;

    public uint MultiScatteringLut;

    public uint SkyViewLut;

    public uint LutSampler;

    public uint HasSky;

    public uint Hdr;

    public uint Lights;

    public uint LightCount;

    public uint LightTiles;

    /// <summary>
    /// 0 on a GPU that traces no rays: nothing is shadowed.
    /// </summary>
    public uint ShadowMask;

    public uint Padding1;

    public uint Padding2;
}

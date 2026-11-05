using System.Runtime.InteropServices;

namespace Magic.GIRendering;

/// <summary>
/// The heap ids of the shadow denoiser's targets, the twin of <c>DenoiseShadows.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ShadowDenoiseConstants
{
    public const int Bytes = 32;

    public uint TileStates;

    public uint SunBlend;

    public uint SunHistory;

    public uint SunDenoised;

    public uint Moments;

    public uint PreviousMoments;

    public uint PreviousMask;

    /// <summary>
    /// 0 on the first frame at a size, when last frame's targets hold nothing to blend with.
    /// </summary>
    public uint HasHistory;
}

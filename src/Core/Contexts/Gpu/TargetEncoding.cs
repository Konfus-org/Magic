namespace Magic.Contexts.Gpu;

/// <summary>
/// What a target's image expects written into it, which its format decides: linear as it is (a render texture, or a
/// window whose sRGB-typed image the GPU encodes), sRGB for an 8-bit UNORM window, PQ (ST 2084 on Rec. 2020) for an
/// HDR10 window, or linear scRGB for a half-float HDR window. Whatever draws into a target encodes for it; a shader
/// reads it as a number among its constants.
/// </summary>
public enum TargetEncoding : uint
{
    Linear,
    Srgb,
    Pq,
    ScRgb,
}

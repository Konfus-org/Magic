namespace Magic.Contexts.Assets;

/// <summary>
/// Pixel format of a texture or volume a pass creates; <see cref="Depth"/> is the device's depth format.
/// </summary>
public enum PassFormat : byte
{
    Rgba8Srgb,
    Rgba8Unorm,
    Rgba16Float,
    R16Float,
    R32Float,
    Rg16Float,
    B10G11R11Float,
    R8Unorm,
    R32Uint,
    Rgba32Float,
    Depth
}

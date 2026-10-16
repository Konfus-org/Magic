namespace Magic.Contexts.Gpu;

/// <summary>
/// One mip level of one layer (or 3D slice, or cube face) of a texture: what a render pass draws into, and what a
/// compute pass writes. The whole level, from its corner.
/// </summary>
public readonly record struct GpuTextureLevel(GpuTexture Texture, uint Level = 0, uint Layer = 0);

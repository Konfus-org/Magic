namespace Magic.Graphics;

/// <summary>
/// A buffer by its place in the bindless heap. A shader reads it through the same id, so nothing is bound.
/// </summary>
/// <remarks>
/// <see cref="None"/> is no buffer.
/// </remarks>
public readonly record struct GpuBuffer(uint Id)
{
    public static GpuBuffer None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A texture by its place in the bindless heap. One id reaches it however a shader reads it.
/// </summary>
/// <remarks>
/// A shader can sample it, read it as a storage image per level, or read it as unsigned integers.
/// <see cref="None"/> is no texture.
/// </remarks>
public readonly record struct GpuTexture(uint Id)
{
    public static GpuTexture None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// An acceleration structure in the heap: a model's triangles, or instances of them, on GPUs that trace rays.
/// </summary>
/// <remarks>
/// Shaders reach it by the id. An instance of a bottom-level one refers to it by its device
/// <paramref name="Address"/>. A bottom-level one can move after it is built, as the GPU provider compacts it: use the
/// handle that the latest ask gave.
/// </remarks>
public readonly record struct GpuAcceleration(uint Id, ulong Address)
{
    public static GpuAcceleration None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A sampler by its place in the bindless heap. <see cref="None"/> is no sampler.
/// </summary>
public readonly record struct GpuSampler(uint Id)
{
    public static GpuSampler None => default;

    public bool IsNone => Id == 0;
}

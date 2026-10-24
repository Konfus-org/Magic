namespace Magic.Contexts.Gpu;

/// <summary>
/// What a pass says it reads or writes: a buffer, a texture whole, or one level of one layer of a texture. A shader
/// that reads through the heap (Shaders/Core/Heap.slang) binds nothing, so its pass lists what it touches, and the
/// GPU waits for each and lays it out before the pass begins (<see cref="GpuComputePass.Reads"/> and the like).
/// </summary>
public readonly record struct GpuResource
{
    private GpuResource(GpuBuffer buffer, GpuTextureLevel level, bool isWhole)
    {
        Buffer = buffer;
        Level = level;
        IsWhole = isWhole;
    }

    /// <summary>
    /// The buffer, or <see cref="GpuBuffer.None"/> for a texture.
    /// </summary>
    public GpuBuffer Buffer { get; }

    /// <summary>
    /// The texture, and the level and layer of it unless <see cref="IsWhole"/>.
    /// </summary>
    public GpuTextureLevel Level { get; }

    /// <summary>
    /// Whether every level of every layer of the texture is meant.
    /// </summary>
    public bool IsWhole { get; }

    public static implicit operator GpuResource(GpuBuffer buffer) => FromGpuBuffer(buffer);

    public static implicit operator GpuResource(GpuTexture texture) => FromGpuTexture(texture);

    public static implicit operator GpuResource(GpuTextureLevel level) => FromGpuTextureLevel(level);

    public static GpuResource FromGpuBuffer(GpuBuffer buffer) => new(buffer, default, isWhole: false);

    public static GpuResource FromGpuTexture(GpuTexture texture)
        => new(default, new GpuTextureLevel(texture), isWhole: true);

    public static GpuResource FromGpuTextureLevel(GpuTextureLevel level) => new(default, level, isWhole: false);
}

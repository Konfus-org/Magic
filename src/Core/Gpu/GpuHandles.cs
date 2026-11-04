namespace Magic.Gpu;

/// <summary>
/// A buffer the GPU provider made, by its place in the bindless heap: the same number a shader reads it through, so a
/// gem hands a shader the handle's id and binds nothing. <see cref="None"/> is no buffer.
/// </summary>
public readonly record struct GpuBuffer(uint Id)
{
    public static GpuBuffer None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A texture the GPU provider made, by its place in the bindless heap: one id reaches it however a shader reads it,
/// sampled, as a storage image per level, or as unsigned integers. <see cref="None"/> is no texture.
/// </summary>
public readonly record struct GpuTexture(uint Id)
{
    public static GpuTexture None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// An acceleration structure of the heap, for the GPUs that trace rays: a model's triangles, or the instances of
/// such, as <see cref="IGraphicsApi.CreateAcceleration(in BottomLevelDescription)"/> and its top-level twin make
/// them. Shaders reach it by the id.
/// </summary>
public readonly record struct GpuAcceleration(uint Id)
{
    public static GpuAcceleration None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A sampler the GPU provider made, by its place in the bindless heap. <see cref="None"/> is no sampler.
/// </summary>
public readonly record struct GpuSampler(uint Id)
{
    public static GpuSampler None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A compute or graphics pipeline the GPU provider built from SPIR-V. <see cref="None"/> is no pipeline.
/// </summary>
public readonly record struct GpuPipeline(uint Id)
{
    public static GpuPipeline None => default;

    public bool IsNone => Id == 0;
}

namespace Magic.Contexts.Gpu;

/// <summary>
/// A texture with the sampler a shader reads it through: one <c>READ(n)</c> with its <c>SAMPLER(n)</c>.
/// </summary>
public readonly record struct GpuSampledTexture(GpuTexture Texture, GpuSampler Sampler);

namespace Magic.Contexts.Gpu;

/// <summary>
/// How a new value is held against the stored one: a pipeline's depth test (reverse-Z, so nearer is greater) or a
/// comparison sampler's (<see cref="GpuSamplerDescription.Compare"/>).
/// </summary>
public enum GpuCompare : byte
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

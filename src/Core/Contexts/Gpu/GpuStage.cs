namespace Magic.Contexts.Gpu;

/// <summary>
/// A shader stage: what a shader is compiled for, and the stage a command binds resources to.
/// </summary>
public enum GpuStage : byte
{
    Vertex,
    Fragment,
    Compute,
}

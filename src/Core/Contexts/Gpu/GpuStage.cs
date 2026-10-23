namespace Magic.Contexts.Gpu;

/// <summary>
/// A shader stage: what a shader is compiled for.
/// </summary>
public enum GpuStage : byte
{
    Vertex,
    Fragment,
    Compute,
}

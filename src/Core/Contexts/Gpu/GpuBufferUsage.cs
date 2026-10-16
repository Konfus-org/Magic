namespace Magic.Contexts.Gpu;

/// <summary>
/// What a buffer may be used for, fixed when it is made.
/// </summary>
[Flags]
public enum GpuBufferUsage : byte
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,

    /// <summary>
    /// The arguments of draws and dispatches the GPU reads from a buffer (<see cref="GpuRenderPass.DrawIndexedFromBuffer"/>, <see cref="GpuComputePass.DispatchFromBuffer"/>).
    /// </summary>
    Arguments = 1 << 2,

    /// <summary>
    /// Read by vertex and fragment shaders as a storage buffer.
    /// </summary>
    GraphicsRead = 1 << 3,

    ComputeRead = 1 << 4,
    ComputeWrite = 1 << 5,
}

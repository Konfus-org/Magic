using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A shader as <see cref="IShaderCompiler.CompileAsync"/> made it: bytecode in a GPU's format, which reads every
/// resource through the heap (Shaders/Core/Heap.slang) and takes one constant block per draw or dispatch.
/// </summary>
public sealed record GpuShader
{
    public required ReadOnlyMemory<byte> Code { get; init; }

    /// <summary>
    /// The function the GPU starts the shader at: <c>main</c>.
    /// </summary>
    public required string EntryPoint { get; init; }

    public required GpuStage Stage { get; init; }

    /// <summary>
    /// Its constant block, in bytes; 0 for one that takes none.
    /// </summary>
    public uint ConstantBytes { get; init; }

    /// <summary>
    /// Compute only: the threads of one group, from <c>[numthreads]</c>.
    /// </summary>
    public uint ThreadCountX { get; init; }

    public uint ThreadCountY { get; init; }

    public uint ThreadCountZ { get; init; }
}

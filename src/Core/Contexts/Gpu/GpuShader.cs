using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A shader as <see cref="IShaderCompiler.CompileAsync"/> made it: bytecode in a GPU's format, with the counts of what it
/// binds, in the binding model of Shaders/Include/Bindings.hlsli, which a GPU needs to make a pipeline of it.
/// </summary>
public sealed record GpuShader
{
    public required ReadOnlyMemory<byte> Code { get; init; }

    /// <summary>
    /// The function the GPU starts the shader at: <c>main</c>, which translation to Metal renames <c>main0</c>.
    /// </summary>
    public required string EntryPoint { get; init; }

    public required GpuStage Stage { get; init; }

    public uint Samplers { get; init; }

    /// <summary>
    /// Read-only storage textures.
    /// </summary>
    public uint StorageTextures { get; init; }

    /// <summary>
    /// Read-only storage buffers.
    /// </summary>
    public uint StorageBuffers { get; init; }

    public uint UniformBuffers { get; init; }

    /// <summary>
    /// Compute only: storage textures it writes.
    /// </summary>
    public uint ReadWriteStorageTextures { get; init; }

    /// <summary>
    /// Compute only: storage buffers it writes.
    /// </summary>
    public uint ReadWriteStorageBuffers { get; init; }

    /// <summary>
    /// Compute only: the threads of one group, from <c>[numthreads]</c>.
    /// </summary>
    public uint ThreadCountX { get; init; }

    public uint ThreadCountY { get; init; }

    public uint ThreadCountZ { get; init; }
}

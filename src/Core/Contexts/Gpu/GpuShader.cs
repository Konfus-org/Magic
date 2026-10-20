using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A shader as <see cref="IShaderCompiler.CompileAsync(string, string, GpuStage, GpuShaderFormat, System.Threading.CancellationToken)"/> made it: bytecode in a GPU's format, and what it binds, read
/// from the code: every resource it uses, by name, at its register in the binding model of
/// Shaders/Include/Bindings.hlsli. The counts of each kind are what a GPU needs to make a pipeline of it; the names are
/// what a renderer binds by.
/// </summary>
public sealed record GpuShader
{
    public required ReadOnlyMemory<byte> Code { get; init; }

    /// <summary>
    /// The function the GPU starts the shader at: <c>main</c>, which translation to Metal renames <c>main0</c>.
    /// </summary>
    public required string EntryPoint { get; init; }

    public required GpuStage Stage { get; init; }

    /// <summary>
    /// What it uses, each at its register. A sampled texture's sampler is at the texture's register and is not listed.
    /// </summary>
    public IReadOnlyList<GpuShaderBinding> Bindings { get; init; } = [];

    /// <summary>
    /// Sampled textures, each with its sampler.
    /// </summary>
    public uint Samplers => CountPast(GpuBindingKind.Sampled);

    /// <summary>
    /// Read-only storage textures.
    /// </summary>
    public uint StorageTextures => CountPast(GpuBindingKind.StorageTexture) - Samplers;

    /// <summary>
    /// Read-only storage buffers.
    /// </summary>
    public uint StorageBuffers => CountPast(GpuBindingKind.StorageBuffer) - Samplers - StorageTextures;

    public uint UniformBuffers => CountPast(GpuBindingKind.Uniform);

    /// <summary>
    /// Compute only: storage textures it writes.
    /// </summary>
    public uint ReadWriteStorageTextures => CountPast(GpuBindingKind.ReadWriteStorageTexture);

    /// <summary>
    /// Compute only: storage buffers it writes.
    /// </summary>
    public uint ReadWriteStorageBuffers => CountPast(GpuBindingKind.ReadWriteStorageBuffer) - ReadWriteStorageTextures;

    /// <summary>
    /// Compute only: the threads of one group, from <c>[numthreads]</c>.
    /// </summary>
    public uint ThreadCountX { get; init; }

    public uint ThreadCountY { get; init; }

    public uint ThreadCountZ { get; init; }

    /// <summary>
    /// One past the highest register a kind uses, or past the kinds before it in its space when it uses none: a space's
    /// registers run sampled textures, then storage textures, then storage buffers (and compute's written textures, then
    /// written buffers), so a kind's count is this less the counts of those before it, and a GPU binds every register
    /// up to it.
    /// </summary>
    private uint CountPast(GpuBindingKind kind)
    {
        uint past = kind switch
        {
            GpuBindingKind.StorageTexture => CountPast(GpuBindingKind.Sampled),
            GpuBindingKind.StorageBuffer => CountPast(GpuBindingKind.StorageTexture),
            GpuBindingKind.ReadWriteStorageBuffer => CountPast(GpuBindingKind.ReadWriteStorageTexture),
            GpuBindingKind.Sampled or GpuBindingKind.Uniform or GpuBindingKind.ReadWriteStorageTexture => 0,
            _ => 0,
        };
        foreach (GpuShaderBinding binding in Bindings)
        {
            past = binding.Kind == kind ? Math.Max(past, binding.Register + 1) : past;
        }

        return past;
    }
}

/// <summary>
/// One resource a shader uses: its name in the HLSL, what it is, and its register in its stage's space, as
/// Shaders/Include/Bindings.hlsli numbers them: <c>READ(n)</c> for what is read, <c>WRITE(n)</c> for what compute
/// writes, <c>UNIFORM(n)</c> for a constant block.
/// </summary>
public readonly record struct GpuShaderBinding(string Name, GpuBindingKind Kind, uint Register);

/// <summary>
/// The kinds of resource the binding model (Shaders/Include/Bindings.hlsli) binds: what a stage reads, its one constant
/// block, and for compute what it writes.
/// </summary>
public enum GpuBindingKind : byte
{
    /// <summary>
    /// A texture read through a sampler, bound as the pair.
    /// </summary>
    Sampled,

    /// <summary>
    /// A texture read texel by texel, with no sampler: one of integers, which no GPU samples.
    /// </summary>
    StorageTexture,

    StorageBuffer,

    Uniform,

    ReadWriteStorageTexture,

    ReadWriteStorageBuffer,
}

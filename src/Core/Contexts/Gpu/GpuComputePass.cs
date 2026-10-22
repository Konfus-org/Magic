namespace Magic.Contexts.Gpu;

/// <summary>
/// A compute pass being recorded (<see cref="GpuCommands.ComputePass"/>): the commands legal inside one, each handing
/// the pass back so they chain. Disposing it ends the pass, so it is a <c>using</c> block. What is bound stays bound
/// until the pass ends; a dispatch uses whatever is bound when it is recorded.
/// </summary>
public readonly record struct GpuComputePass : IDisposable
{
    private readonly GpuCommands _commands;

    internal GpuComputePass(GpuCommands commands) => _commands = commands;

    public void Dispose() => _commands.EndComputePass();

    /// <summary>
    /// The compute pipeline the dispatches after it use.
    /// </summary>
    public GpuComputePass Pipeline(GpuPipeline pipeline)
    {
        _commands.Pipeline(pipeline);

        return this;
    }

    /// <summary>
    /// The buffers the shader reads, from register 0, as its <c>READ(n)</c>s number them after its textures
    /// (Shaders/Include/Bindings.hlsli).
    /// </summary>
    public GpuComputePass Buffers(ReadOnlySpan<GpuBuffer> buffers)
    {
        _commands.Buffers(GpuStage.Compute, buffers);

        return this;
    }

    /// <summary>
    /// The textures the shader reads texel by texel, with no sampler, from register 0, as its <c>READ(n)</c>s number
    /// them after its sampled textures.
    /// </summary>
    public GpuComputePass RawTextures(ReadOnlySpan<GpuTexture> textures)
    {
        _commands.RawTextures(GpuStage.Compute, textures);

        return this;
    }

    /// <summary>
    /// The textures the shader reads through a sampler, from register 0: the nth is its <c>READ(n)</c> with
    /// <c>SAMPLER(n)</c>.
    /// </summary>
    public GpuComputePass Textures(ReadOnlySpan<GpuSampledTexture> textures)
    {
        _commands.Textures(GpuStage.Compute, textures);

        return this;
    }

    /// <summary>
    /// The shader's one constant block (<c>UNIFORM(0)</c>), copied now: a struct laid out as the <c>cbuffer</c> is, in
    /// whole 16-byte rows.
    /// </summary>
    public GpuComputePass Constants<T>(in T constants)
        where T : unmanaged
    {
        _commands.Constants(GpuStage.Compute, in constants);

        return this;
    }

    public GpuComputePass Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        _commands.Dispatch(groupsX, groupsY, groupsZ);

        return this;
    }

    /// <summary>
    /// The groups read from <paramref name="arguments"/> at <paramref name="offset"/>: three <c>uint</c>s.
    /// </summary>
    public GpuComputePass DispatchFromBuffer(GpuBuffer arguments, uint offset = 0)
    {
        _commands.DispatchFromBuffer(arguments, offset);

        return this;
    }
}

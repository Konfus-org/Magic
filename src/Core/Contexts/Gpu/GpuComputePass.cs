namespace Magic.Contexts.Gpu;

/// <summary>
/// A compute pass being recorded (<see cref="GpuCommands.ComputePass()"/>): the commands legal inside one, each handing
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
    /// What the pass's shaders read through the heap (Shaders/Core/Heap.slang): each is waited for and laid out to
    /// read before the pass begins, wherever in the pass this is recorded.
    /// </summary>
    public GpuComputePass Reads(ReadOnlySpan<GpuResource> resources)
    {
        _commands.Reads(resources);

        return this;
    }

    /// <summary>
    /// What the pass's shaders write through the heap: each is waited for and laid out to write before the pass begins.
    /// </summary>
    public GpuComputePass Writes(ReadOnlySpan<GpuResource> resources)
    {
        _commands.Writes(resources);

        return this;
    }

    /// <summary>
    /// The shader's one constant block, copied now: at most 128 bytes, laid out as its uniform entry point parameters
    /// are, one after another, for the dispatches after it.
    /// </summary>
    public GpuComputePass Constants<T>(in T constants)
        where T : unmanaged
    {
        _commands.Constants(in constants);

        return this;
    }

    /// <summary>
    /// The shader's one constant block as bytes, as <see cref="Constants{T}"/> takes it, for constants whose layout is
    /// known only as the pass is recorded.
    /// </summary>
    public GpuComputePass Constants(ReadOnlySpan<byte> constants)
    {
        _commands.Constants(constants);

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

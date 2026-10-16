namespace Magic.Contexts.Gpu;

/// <summary>
/// A named stretch of the frame being recorded (<see cref="GpuCommands.DebugGroup"/>), which a GPU debugger shows the
/// commands under. Disposing it ends the group, so it is a <c>using</c> block.
/// </summary>
public readonly record struct GpuDebugGroup : IDisposable
{
    private readonly GpuCommands _commands;

    internal GpuDebugGroup(GpuCommands commands) => _commands = commands;

    public void Dispose() => _commands.EndDebugGroup();
}

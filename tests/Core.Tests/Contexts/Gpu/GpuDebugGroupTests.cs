using Magic.Contexts.Gpu;

namespace Magic.Tests.Contexts.Gpu;

public sealed class GpuDebugGroupTests
{
    [Fact]
    public void Disposing_ends_the_group()
    {
        GpuCommands commands = new();
        GpuDebugGroup group = commands.DebugGroup("Shadows");

        group.Dispose();

        Assert.Equal(GpuCommandType.EndDebugGroup, commands.Commands[^1].Type);
    }
}

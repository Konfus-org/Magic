using Magic.Contexts.Gpu;

namespace Magic.Tests.Contexts.Gpu;

public sealed class GpuComputePassTests
{
    [Fact]
    public void Disposing_ends_the_pass()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);

        pass.Dispose();

        Assert.Equal(GpuCommandType.EndComputePass, commands.Commands[^1].Type);
    }

    [Fact]
    public void Dispatch_records_its_groups()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);

        pass.Dispatch(4, 2, 3);

        Assert.Equal(new GpuCommand(GpuCommandType.Dispatch, GroupsX: 4, GroupsY: 2, GroupsZ: 3), commands.Commands[^1]);
    }

    [Fact]
    public void DispatchFromBuffer_records_where_the_groups_are()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);
        GpuBuffer arguments = new(9);

        pass.DispatchFromBuffer(arguments, 12);

        Assert.Equal(new GpuCommand(GpuCommandType.DispatchFromBuffer, Buffer: arguments, Offset: 12), commands.Commands[^1]);
    }

    [Fact]
    public void Pipeline_records_the_pipeline()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);
        GpuPipeline pipeline = new(3);

        pass.Pipeline(pipeline);

        Assert.Equal(pipeline, commands.Commands[^1].Pipeline);
    }

    [Fact]
    public void Buffers_bind_to_the_compute_stage()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);

        pass.Buffers([new GpuBuffer(1)]);

        Assert.Equal(GpuStage.Compute, commands.Commands[^1].Stage);
    }

    [Fact]
    public void Textures_records_the_sampled_textures()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);
        GpuSampledTexture[] textures = [new GpuSampledTexture(new GpuTexture(1), new GpuSampler(2))];

        pass.Textures(textures);

        Assert.Equal(textures, commands.GetSampled(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void Constants_records_the_structs_bytes()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);

        pass.Constants(0x01020304u);

        Assert.Equal(BitConverter.GetBytes(0x01020304u), commands.GetBytes(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void A_command_hands_the_pass_back_to_chain_the_next()
    {
        GpuCommands commands = new();
        GpuComputePass pass = commands.ComputePass([], []);

        GpuComputePass chained = pass.Dispatch(1);

        Assert.Equal(pass, chained);
    }
}

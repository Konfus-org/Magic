using Magic.Contexts.Gpu;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Tests.Contexts.Gpu;

public sealed class GpuRenderPassTests
{
    [Fact]
    public void Disposing_ends_the_pass()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        pass.Dispose();

        Assert.Equal(GpuCommandType.EndRenderPass, commands.Commands[^1].Type);
    }

    [Fact]
    public void Draw_records_its_counts()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        pass.Draw(3, instances: 2, firstVertex: 1, firstInstance: 4);

        Assert.Equal(
            new GpuCommand(GpuCommandType.Draw, Count: 3, Instances: 2, First: 1, FirstInstance: 4),
            commands.Commands[^1]);
    }

    [Fact]
    public void DrawIndexed_records_its_counts()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        pass.DrawIndexed(6, firstIndex: 3, vertexOffset: -1, instances: 2, firstInstance: 5);

        Assert.Equal(
            new GpuCommand(
                GpuCommandType.DrawIndexed,
                Count: 6,
                Instances: 2,
                First: 3,
                VertexOffset: -1,
                FirstInstance: 5),
            commands.Commands[^1]);
    }

    [Fact]
    public void DrawIndexedFromBuffer_records_where_the_arguments_are()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuBuffer arguments = new(9);

        pass.DrawIndexedFromBuffer(arguments, 40, 2);

        Assert.Equal(
            new GpuCommand(GpuCommandType.DrawIndexedFromBuffer, Buffer: arguments, Count: 2, Offset: 40),
            commands.Commands[^1]);
    }

    [Fact]
    public void Pipeline_records_the_pipeline()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuPipeline pipeline = new(3);

        pass.Pipeline(pipeline);

        Assert.Equal(pipeline, commands.Commands[^1].Pipeline);
    }

    [Fact]
    public void Viewport_records_its_area()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        Rectangle area = new(10, 20, 300, 400);

        pass.Viewport(area);

        Assert.Equal(area, commands.Commands[^1].Area);
    }

    [Fact]
    public void VertexBuffers_records_the_buffers()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuBuffer[] buffers = [new GpuBuffer(1), new GpuBuffer(2)];

        pass.VertexBuffers(buffers);

        Assert.Equal(buffers, commands.GetBuffers(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void IndexBuffer_records_the_buffer_and_its_index_size()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuBuffer buffer = new(6);

        pass.IndexBuffer(buffer, GpuIndexSize.Bits32);

        GpuCommand bound = commands.Commands[^1];
        Assert.Equal((buffer, GpuIndexSize.Bits32), (bound.Buffer, bound.IndexSize));
    }

    [Fact]
    public void Buffers_records_the_stage_that_reads_them()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        pass.Buffers(GpuStage.Fragment, [new GpuBuffer(1)]);

        Assert.Equal(GpuStage.Fragment, commands.Commands[^1].Stage);
    }

    [Fact]
    public void Buffers_records_the_buffers()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuBuffer[] buffers = [new GpuBuffer(1), new GpuBuffer(2)];

        pass.Buffers(GpuStage.Fragment, buffers);

        Assert.Equal(buffers, commands.GetBuffers(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void Textures_records_the_sampled_textures()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuSampledTexture[] textures = [new GpuSampledTexture(new GpuTexture(1), new GpuSampler(2))];

        pass.Textures(GpuStage.Fragment, textures);

        Assert.Equal(textures, commands.GetSampled(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void RawTextures_records_the_textures()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);
        GpuTexture[] textures = [new GpuTexture(1), new GpuTexture(2)];

        pass.RawTextures(GpuStage.Vertex, textures);

        Assert.Equal(textures, commands.GetTextures(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void Constants_records_the_structs_bytes()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        pass.Constants(GpuStage.Vertex, 0x01020304u);

        Assert.Equal(BitConverter.GetBytes(0x01020304u), commands.GetBytes(commands.Commands[^1]).ToArray());
    }

    [Fact]
    public void A_command_hands_the_pass_back_to_chain_the_next()
    {
        GpuCommands commands = new();
        GpuRenderPass pass = commands.RenderPass([], GpuContents.Keep);

        GpuRenderPass chained = pass.Draw(3);

        Assert.Equal(pass, chained);
    }
}

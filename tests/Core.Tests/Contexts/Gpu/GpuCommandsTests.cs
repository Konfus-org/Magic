using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.Tests.Contexts.Gpu;

public sealed class GpuCommandsTests
{
    [Fact]
    public void RenderPass_records_its_colour_targets()
    {
        GpuCommands commands = new();
        GpuTextureLevel[] colors = [new GpuTextureLevel(new GpuTexture(1)), new GpuTextureLevel(
            new GpuTexture(2),
            Level: 1)];

        commands.RenderPass(colors, GpuContents.Keep).Dispose();

        Assert.Equal(colors, commands.GetLevels(commands.Commands[0]).ToArray());
    }

    [Fact]
    public void RenderPass_records_its_depth_target()
    {
        GpuCommands commands = new();
        GpuTextureLevel depth = new(new GpuTexture(3));

        commands.RenderPass([], GpuContents.Keep, depth: depth).Dispose();

        Assert.Equal(depth, commands.Commands[0].Depth);
    }

    [Fact]
    public void RenderPass_records_what_happens_to_the_targets_contents()
    {
        GpuCommands commands = new();

        commands.RenderPass([], GpuContents.Discard).Dispose();

        Assert.Equal(GpuContents.Discard, commands.Commands[0].Contents);
    }

    [Fact]
    public void RenderPass_records_the_clear_colour()
    {
        GpuCommands commands = new();
        Color clear = new(0.1f, 0.2f, 0.3f);

        commands.RenderPass([], GpuContents.Clear, clear).Dispose();

        Assert.Equal(clear, commands.Commands[0].Clear);
    }

    [Fact]
    public void CopyTexture_records_the_source_then_the_destination()
    {
        GpuCommands commands = new();
        GpuTextureRegion from = new(new GpuTexture(1), Width: 16, Height: 16);
        GpuTextureRegion to = new(new GpuTexture(2), Level: 1);

        commands.CopyTexture(from, to);

        GpuTextureRegion[] expected = [from, to];
        Assert.Equal(expected, commands.GetRegions(commands.Commands[0]).ToArray());
    }

    [Fact]
    public void CopyBuffer_records_the_source_then_the_destination()
    {
        GpuCommands commands = new();
        GpuBuffer from = new(1);
        GpuBuffer to = new(2);

        commands.CopyBuffer(from, 0, to, 0, 64);

        GpuBuffer[] expected = [from, to];
        Assert.Equal(expected, commands.GetBuffers(commands.Commands[0]).ToArray());
    }

    [Fact]
    public void CopyBuffer_records_the_offsets_and_how_many_bytes()
    {
        GpuCommands commands = new();

        commands.CopyBuffer(new GpuBuffer(1), 16, new GpuBuffer(2), 32, 64);

        GpuCommand copy = commands.Commands[0];
        Assert.Equal((16u, 32u, 64u), (copy.First, copy.Offset, copy.Count));
    }

    [Fact]
    public void GenerateMipmaps_records_the_texture()
    {
        GpuCommands commands = new();
        GpuTexture texture = new(7);

        commands.GenerateMipmaps(texture);

        Assert.Equal(texture, commands.Commands[0].Texture);
    }

    [Fact]
    public void DebugGroup_records_its_name_as_utf8()
    {
        GpuCommands commands = new();

        commands.DebugGroup("Lumière").Dispose();

        Assert.Equal(Encoding.UTF8.GetBytes("Lumière"), commands.GetBytes(commands.Commands[0]).ToArray());
    }

    [Fact]
    public void Commands_are_in_the_order_recorded()
    {
        GpuCommands commands = new();
        commands.GenerateMipmaps(new GpuTexture(1))
            .CopyBuffer(new GpuBuffer(1), 0, new GpuBuffer(2), 0, 4);

        GpuCommandType[] expected = [GpuCommandType.GenerateMipmaps, GpuCommandType.CopyBuffer];
        GpuCommandType[] types = [commands.Commands[0].Type, commands.Commands[1].Type];
        Assert.Equal(expected, types);
    }

    [Fact]
    public void Clear_empties_the_commands()
    {
        GpuCommands commands = new();
        commands.GenerateMipmaps(new GpuTexture(1));

        commands.Clear();

        Assert.Equal(0, commands.Count);
    }
}

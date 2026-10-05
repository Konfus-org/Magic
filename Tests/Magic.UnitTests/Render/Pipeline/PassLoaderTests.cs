using DeferredRendererGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Xunit;

namespace Magic.UnitTests.Render.Pipeline;

public sealed class PassLoaderTests
{
    [Fact]
    public void A_post_reads_its_inputs_in_order_and_writes_its_output()
    {
        Post post = new() { Shader = new Handle<Shader>(5), Inputs = ["Hdr", "Depth"], Output = new PostOutput { Name = "Ldr" } };

        Pass pass = PassLoader.FromPost(post);

        Assert.Equal(["Hdr", "Depth"], pass.Reads.Select(read => read.Name));
        Assert.Equal("Ldr", Assert.Single(pass.Writes).Name);
    }

    [Fact]
    public void A_post_writing_an_engine_target_creates_nothing()
    {
        Post post = new() { Shader = new Handle<Shader>(5), Inputs = ["Hdr"], Output = new PostOutput { Name = "Ldr" } };

        Pass pass = PassLoader.FromPost(post);

        Assert.Empty(pass.Creates);
    }

    [Fact]
    public void A_post_writing_a_new_target_creates_it_at_its_scale_and_format()
    {
        Post post = new() { Shader = new Handle<Shader>(5), Inputs = ["Hdr"], Output = new PostOutput { Name = "Bloom", Format = PassFormat.Rgba16Float, Scale = 0.5f } };

        Pass pass = PassLoader.FromPost(post);

        PassCreate create = Assert.Single(pass.Creates);
        Assert.Equal(("Bloom", ResourceKind.Texture, PassFormat.Rgba16Float, 0.5f), (create.Name, create.Kind, create.Format, create.Scale));
    }

    [Fact]
    public void A_post_is_dispatched_over_its_output()
    {
        Post post = new() { Shader = new Handle<Shader>(5), Inputs = ["Hdr"], Output = new PostOutput { Name = "Ldr" } };

        Pass pass = PassLoader.FromPost(post);

        Assert.Equal("output:Ldr", pass.Dispatch.Per);
    }

    [Theory]
    [InlineData("Passes/Tonemap.post", true)]
    [InlineData("Passes/Core/Lighting.pass", false)]
    [InlineData(null, false)]
    public void A_post_is_told_by_its_extension(string? path, bool expected)
    {
        bool isPost = PassLoader.IsPost(path);

        Assert.Equal(expected, isPost);
    }
}

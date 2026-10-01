using Magic.Contexts.Assets;
using Magic.Services;
using SDLGem;
using SDLImageGem;
using Xunit;

namespace Magic.IntegrationTests.Assets;

/// <summary>The texture loader over the real SDL3_image, decoding the repo's 8 x 8 checkerboard.</summary>
[Collection(SdlCollection.Name)]
public sealed class SdlImageTests : IDisposable
{
    private readonly Sdl _sdl = new(new Project { Name = "Tests" });

    public void Dispose()
    {
        _sdl.Dispose();
    }

    [Fact]
    public void A_texture_takes_the_size_of_its_image()
    {
        Texture texture = new() { Path = "Textures/Checkerboard.png" };

        new SdlImage().Load(texture, Resources.Read(texture.Path));

        Assert.Equal((8, 8), (texture.Width, texture.Height));
    }

    [Fact]
    public void A_mipmapped_texture_gets_a_level_per_halving()
    {
        Texture texture = new() { Path = "Textures/Checkerboard.png", Mipmaps = true };

        new SdlImage().Load(texture, Resources.Read(texture.Path));

        Assert.Equal([8, 4, 2, 1], texture.Levels.Select(level => level.Width));
    }

    [Fact]
    public void Without_mipmaps_a_texture_has_one_level()
    {
        Texture texture = new() { Path = "Textures/Checkerboard.png", Mipmaps = false };

        new SdlImage().Load(texture, Resources.Read(texture.Path));

        Assert.Single(texture.Levels);
    }

    [Fact]
    public void The_levels_are_packed_one_after_the_other()
    {
        Texture texture = new() { Path = "Textures/Checkerboard.png", Mipmaps = true };

        new SdlImage().Load(texture, Resources.Read(texture.Path));

        Assert.Equal(new TextureLevel(1, 1, (8 * 8 * 4) + (4 * 4 * 4) + (2 * 2 * 4), 4), texture.Levels[^1]);
    }

    [Fact]
    public void A_vector_image_is_rasterised_at_the_size_its_sidecar_asks_for()
    {
        Texture texture = new() { Path = "Icons/Mage.svg", Size = 256, Mipmaps = false };

        new SdlImage().Load(texture, Resources.Read(texture.Path));

        Assert.Equal(256, texture.Width);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_refused()
    {
        Texture texture = new() { Path = "Textures/Broken.png" };

        Action load = () => new SdlImage().Load(texture, [1, 2, 3]);

        Assert.Throws<InvalidOperationException>(load);
    }
}

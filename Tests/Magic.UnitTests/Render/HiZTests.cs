using DeferredRendererGem;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class HiZTests
{
    [Fact]
    public void Level_0_is_half_the_view_rounded_up()
    {
        (int width, int height, _, _) = HiZ.Size(1921, 1080);

        Assert.Equal((961, 540), (width, height));
    }

    [Fact]
    public void The_levels_go_down_to_one_texel()
    {
        (_, _, int levels, _) = HiZ.Size(64, 64);

        Assert.Equal(6, levels);
    }

    [Fact]
    public void The_floats_hold_every_level()
    {
        (_, _, _, uint floats) = HiZ.Size(8, 8);

        Assert.Equal(4u * 4 + 2 * 2 + 1, floats);
    }

    [Fact]
    public void A_huge_view_stops_at_the_most_levels()
    {
        (_, _, int levels, _) = HiZ.Size(16384, 16384);

        Assert.Equal(HiZ.MaxLevels, levels);
    }
}

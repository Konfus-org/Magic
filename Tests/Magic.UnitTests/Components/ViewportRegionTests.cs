using Magic.Contexts.Components;
using Magic.Extensions;
using System.Drawing;
using Xunit;

namespace Magic.UnitTests.Components;

public sealed class ViewportRegionTests
{
    [Theory]
    [InlineData(ViewportRegion.Full, 0, 0, 800, 600)]
    [InlineData(ViewportRegion.BottomRight, 400, 300, 400, 300)]
    [InlineData(ViewportRegion.LeftHalf, 0, 0, 400, 600)]
    public void A_region_covers_its_part_of_the_target(ViewportRegion region, int x, int y, int width, int height)
    {
        Rectangle pixels = region.ToPixels(800, 600);

        Assert.Equal(new Rectangle(x, y, width, height), pixels);
    }

    [Fact]
    public void An_odd_size_leaves_no_gap_between_two_halves()
    {
        Rectangle right = ViewportRegion.RightHalf.ToPixels(801, 600);

        Assert.Equal(new Rectangle(400, 0, 401, 600), right);
    }
}

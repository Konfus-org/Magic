using Magic.Contexts.Components;
using Magic.Extensions;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace Magic.Tests.Extensions;

public sealed class ScreenSectionExtensionsTests
{
    [Theory]
    [InlineData(ScreenSection.Full, 0, 0, 800, 600)]
    [InlineData(ScreenSection.TopLeft, 0, 0, 400, 300)]
    [InlineData(ScreenSection.TopRight, 400, 0, 400, 300)]
    [InlineData(ScreenSection.BottomLeft, 0, 300, 400, 300)]
    [InlineData(ScreenSection.BottomRight, 400, 300, 400, 300)]
    [InlineData(ScreenSection.Top, 0, 0, 800, 300)]
    [InlineData(ScreenSection.Bottom, 0, 300, 800, 300)]
    [InlineData(ScreenSection.Left, 0, 0, 400, 600)]
    [InlineData(ScreenSection.Right, 400, 0, 400, 600)]
    public void AreaIn_splits_the_window_at_its_middle(ScreenSection section, int x, int y, int width, int height)
    {
        Size window = new(800, 600);

        Rectangle area = section.AreaIn(window);

        Assert.Equal(new Rectangle(x, y, width, height), area);
    }

    [Fact]
    public void AreaIn_gives_an_odd_pixel_across_to_the_right()
    {
        Size window = new(801, 600);

        Rectangle area = ScreenSection.Right.AreaIn(window);

        Assert.Equal(new Rectangle(400, 0, 401, 600), area);
    }

    [Fact]
    public void AreaIn_gives_an_odd_pixel_down_to_the_bottom()
    {
        Size window = new(800, 601);

        Rectangle area = ScreenSection.Bottom.AreaIn(window);

        Assert.Equal(new Rectangle(0, 300, 800, 301), area);
    }
}

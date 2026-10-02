using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.UnitTests.Fakes;
using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Extensions;

public sealed class RenderingExtensionsTests
{
    [Fact]
    public void A_screenshot_writes_what_the_window_showed_as_a_png()
    {
        FakeRendering rendering = new() { Shown = new CapturedFrame(1, 1, [255, 0, 0, 255]) };
        FakeFileSystem files = new();

        rendering.Screenshot(new FakeWindows(), files, "shot.png");

        Assert.Equal(new byte[] { 255, 0, 0, 255 }.Png(1, 1), files.Written["shot.png"]);
    }

    [Fact]
    public void A_screenshot_of_a_window_that_showed_nothing_fails()
    {
        FakeRendering rendering = new();

        Result taken = rendering.Screenshot(new FakeWindows(), new FakeFileSystem(), "shot.png");

        Assert.True(taken.Failed);
    }

    [Fact]
    public void A_failed_screenshot_writes_no_file()
    {
        FakeRendering rendering = new();
        FakeFileSystem files = new();

        rendering.Screenshot(new FakeWindows(), files, "shot.png");

        Assert.Empty(files.Written);
    }
}

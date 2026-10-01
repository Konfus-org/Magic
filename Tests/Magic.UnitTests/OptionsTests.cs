using Magic.Interfaces;
using Xunit;

namespace Magic.UnitTests;

public sealed class OptionsTests
{
    [Fact]
    public void The_smallest_runnable_values_are_accepted()
    {
        Options options = Runnable();

        string? error = options.Validate();

        Assert.Null(error);
    }

    [Fact]
    public void A_negative_lifetime_is_refused()
    {
        Options options = Runnable();
        options.Lifetime = -1;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_negative_screenshot_count_is_refused()
    {
        Options options = Runnable();
        options.Screenshots = -1;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_screenshot_delay_below_one_is_refused()
    {
        Options options = Runnable();
        options.ScreenshotDelay = 0;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_screenshot_interval_below_one_is_refused()
    {
        Options options = Runnable();
        options.ScreenshotInterval = 0;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_width_below_one_is_refused()
    {
        Options options = Runnable();
        options.Width = 0;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_height_below_one_is_refused()
    {
        Options options = Runnable();
        options.Height = 0;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void Verbose_is_refused_as_a_log_level()
    {
        Options options = Runnable();
        options.LogLevel = LogLevel.Verbose;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void Screenshots_are_refused_without_a_window()
    {
        Options options = Runnable();
        options.Headless = true;
        options.Screenshots = 1;

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("Vsync=false")]  // no section
    [InlineData("Render.Vsync")] // no value
    public void A_malformed_set_is_refused(string set)
    {
        Options options = Runnable();
        options.Set = [set];

        string? error = options.Validate();

        Assert.NotNull(error);
    }

    [Fact]
    public void A_section_key_and_value_set_is_accepted()
    {
        Options options = Runnable();
        options.Set = ["Render.Vsync=false"];

        string? error = options.Validate();

        Assert.Null(error);
    }

    [Theory]
    [InlineData(0, 10, 50, 0)]   // no screenshots: nothing to wait for
    [InlineData(1, 10, 50, 10)]  // just the delay
    [InlineData(3, 10, 50, 110)] // 10, 60, 110
    public void The_last_screenshot_frame_is_the_delay_plus_the_intervals(int count, int delay, int interval, long expected)
    {
        Options options = new() { Screenshots = count, ScreenshotDelay = delay, ScreenshotInterval = interval };

        long last = options.LastScreenshotFrame;

        Assert.Equal(expected, last);
    }

    /// <summary>Every value at the lowest Validate accepts, so a test changes only the one it is about.</summary>
    private static Options Runnable()
    {
        return new Options { ScreenshotDelay = 1, ScreenshotInterval = 1, Width = 1, Height = 1, LogLevel = LogLevel.Debug };
    }
}

using CommandLine;
using Magic.Interfaces;
using Xunit;

namespace Magic.Tests;

public sealed class OptionsTests
{
    private static Options Parse(params string[] args)
    {
        using Parser parser = new(with =>
        {
            with.HelpWriter = null;
            with.CaseInsensitiveEnumValues = true;
        });
        ParserResult<Options> result = parser.ParseArguments<Options>(args);
        Assert.IsType<Parsed<Options>>(result);
        return ((Parsed<Options>)result).Value;
    }

    [Fact]
    public void Defaults_run_forever_with_a_window_and_no_screenshots()
    {
        Options options = Parse();

        Assert.Null(options.Project);
        Assert.Null(options.Root);
        Assert.Null(options.Gems);
        Assert.Equal(0, options.Lifetime);
        Assert.Equal(0, options.Screenshots);
        Assert.False(options.Headless);
        Assert.Equal(800, options.Width);
        Assert.Equal(600, options.Height);
        Assert.Equal(WindowMode.Windowed, options.WindowMode);
        Assert.Equal(LogLevel.Debug, options.LogLevel);
        Assert.False(options.FailOnError);
        Assert.Null(options.Validate());
        Assert.Equal(0, options.LastScreenshotFrame);
    }

    [Fact]
    public void Every_option_parses()
    {
        Options options = Parse(
            "--project", @"C:\Games\Demo", "--root", @"D:\Out", "--gems", @"D:\Gems",
            "--lifetime", "300", "--screenshots", "3", "--screenshot-delay", "10", "--screenshot-interval", "50",
            "--width", "1280", "--height", "720", "--window-mode", "borderless", "--log-level", "warning", "--fail-on-error");

        Assert.Equal(@"C:\Games\Demo", options.Project);
        Assert.Equal(@"D:\Out", options.Root);
        Assert.Equal(@"D:\Gems", options.Gems);
        Assert.Equal(300, options.Lifetime);
        Assert.Equal(3, options.Screenshots);
        Assert.Equal(10, options.ScreenshotDelay);
        Assert.Equal(50, options.ScreenshotInterval);
        Assert.Equal(1280, options.Width);
        Assert.Equal(720, options.Height);
        Assert.Equal(WindowMode.Borderless, options.WindowMode);
        Assert.Equal(LogLevel.Warning, options.LogLevel);
        Assert.True(options.FailOnError);
        Assert.Null(options.Validate());
        Assert.Equal(110, options.LastScreenshotFrame); // 10, 60, 110
    }

    [Fact]
    public void Headless_parses_as_a_flag()
    {
        Assert.True(Parse("--headless", "--lifetime", "1").Headless);
    }

    [Fact]
    public void Unknown_option_is_a_parse_error()
    {
        using Parser parser = new(with => with.HelpWriter = null);
        Assert.IsType<NotParsed<Options>>(parser.ParseArguments<Options>(["--bogus"]));
    }

    [Theory]
    [InlineData("--lifetime", "-1")]
    [InlineData("--screenshots", "-1")]
    [InlineData("--screenshot-delay", "0")]
    [InlineData("--screenshot-interval", "0")]
    [InlineData("--width", "0")]
    [InlineData("--height", "0")]
    public void Out_of_range_values_are_refused_by_validate(string option, string value)
    {
        Assert.NotNull(Parse(option, value).Validate());
    }

    [Fact]
    public void Screenshots_need_a_window()
    {
        Assert.NotNull(Parse("--headless", "--screenshots", "1").Validate());
        Assert.Null(Parse("--headless").Validate());
    }
}

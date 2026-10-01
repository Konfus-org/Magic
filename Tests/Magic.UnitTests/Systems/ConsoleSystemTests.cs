using Magic.Systems.DebugUI;
using Xunit;

namespace Magic.UnitTests.Systems;

public sealed class ConsoleSystemTests
{
    [Theory]
    [InlineData("give sword 2", new[] { "give", "sword", "2" })]
    [InlineData("  give   sword  ", new[] { "give", "sword" })]         // runs of whitespace separate once
    [InlineData("give \"long sword\" 2", new[] { "give", "long sword", "2" })]
    [InlineData("say \"\"", new[] { "say", "" })]                       // empty quotes are an empty argument
    public void A_line_splits_on_whitespace_outside_quotes(string line, string[] expected)
    {
        string[] words = ConsoleSystem.Split(line);

        Assert.Equal(expected, words);
    }
}

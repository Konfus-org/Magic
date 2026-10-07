using Magic.Contexts.Debug;
using Magic.Services;
using System.Numerics;
using Xunit;

namespace DebugTests;

public sealed class DebugUITests
{
    private static readonly Vector3 Somewhere = new(1, 2, 3);

    [Fact]
    public void A_reported_line_shows()
    {
        DebugUI debug = new();
        debug.Info("Saved");

        List<DebugEntry> shown = Take(debug, positioned: false);

        Assert.Equal(["Saved"], shown.Select(entry => entry.Text));
    }

    [Fact]
    public void A_line_reported_again_while_it_shows_is_one_line_counted_twice()
    {
        DebugUI debug = new();
        debug.Info("Saved");
        debug.Info("Saved");

        List<DebugEntry> shown = Take(debug, positioned: false);

        Assert.Equal(2, Assert.Single(shown).Count);
    }

    [Fact]
    public void World_text_is_taken_with_its_position()
    {
        DebugUI debug = new();
        debug.Text(Somewhere, "Broken");

        List<DebugEntry> shown = Take(debug, positioned: true);

        Assert.Equal(Somewhere, Assert.Single(shown).Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_entry_is_taken_only_with_its_kind(bool positioned)
    {
        DebugUI debug = new();
        if (positioned)
            debug.Text(Somewhere, "Broken");
        else
            debug.Info("Broken");

        List<DebugEntry> shown = Take(debug, positioned: !positioned);

        Assert.Empty(shown);
    }

    [Fact]
    public void World_text_not_reported_again_is_gone_after_the_frame_that_showed_it()
    {
        DebugUI debug = new();
        debug.Text(Somewhere, "Broken");
        Take(debug, positioned: true);

        List<DebugEntry> shown = Take(debug, positioned: true);

        Assert.Empty(shown);
    }

    [Fact]
    public void Lines_are_taken_in_the_order_they_were_first_reported()
    {
        DebugUI debug = new();
        debug.Info("First");
        debug.Info("Second");
        debug.Info("First");

        List<DebugEntry> shown = Take(debug, positioned: false);

        Assert.Equal(["First", "Second"], shown.Select(entry => entry.Text));
    }

    private static List<DebugEntry> Take(DebugUI debug, bool positioned)
    {
        List<DebugEntry> shown = [];
        debug.Take(positioned, shown);

        return shown;
    }
}

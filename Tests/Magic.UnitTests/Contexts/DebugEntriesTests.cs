using Magic.Contexts.Debug;
using System.Drawing;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Contexts;

public sealed class DebugEntriesTests
{
    private static readonly Vector3 Somewhere = new(1, 2, 3);

    [Fact]
    public void A_reported_line_shows_until_its_seconds_are_over()
    {
        DebugEntries entries = new();
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 10, counted: true);
        Take(entries, now: 10, positioned: false); // the frame it was reported in

        List<DebugEntry> shown = Take(entries, now: 14.9, positioned: false);

        Assert.Single(shown);
    }

    [Fact]
    public void A_line_whose_seconds_are_over_is_gone()
    {
        DebugEntries entries = new();
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 10, counted: true);
        Take(entries, now: 10, positioned: false);

        List<DebugEntry> shown = Take(entries, now: 15, positioned: false);

        Assert.Empty(shown);
    }

    [Fact]
    public void A_line_reported_again_while_it_shows_is_still_one_line()
    {
        DebugEntries entries = new();
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 10, counted: true);
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 11, counted: true);

        List<DebugEntry> shown = Take(entries, now: 11, positioned: false);

        Assert.Single(shown);
    }

    [Fact]
    public void A_counted_repeat_adds_to_the_count()
    {
        DebugEntries entries = new();
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 10, counted: true);
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 11, counted: true);

        List<DebugEntry> shown = Take(entries, now: 11, positioned: false);

        Assert.Equal(2, shown[0].Count);
    }

    [Fact]
    public void A_repeat_shows_for_its_seconds_from_the_repeat()
    {
        DebugEntries entries = new();
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 10, counted: true);
        entries.Report("Saved", Color.White, null, seconds: 5f, now: 14, counted: true);
        Take(entries, now: 14, positioned: false);

        List<DebugEntry> shown = Take(entries, now: 18, positioned: false);

        Assert.Single(shown);
    }

    [Fact]
    public void An_entry_with_no_seconds_shows_in_the_frame_it_was_reported_in()
    {
        DebugEntries entries = new();
        entries.Report("Broken", Color.Red, Somewhere, seconds: 0f, now: 10, counted: false);

        List<DebugEntry> shown = Take(entries, now: 10.016, positioned: true);

        Assert.Single(shown);
    }

    [Fact]
    public void An_entry_with_no_seconds_is_gone_the_frame_it_is_not_reported_in()
    {
        DebugEntries entries = new();
        entries.Report("Broken", Color.Red, Somewhere, seconds: 0f, now: 10, counted: false);
        Take(entries, now: 10.016, positioned: true);

        List<DebugEntry> shown = Take(entries, now: 10.032, positioned: true);

        Assert.Empty(shown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_entry_is_taken_only_with_its_kind(bool positioned)
    {
        DebugEntries entries = new();
        entries.Report("Broken", Color.Red, positioned ? Somewhere : null, seconds: 5f, now: 10, counted: false);

        List<DebugEntry> shown = Take(entries, now: 10, positioned: !positioned);

        Assert.Empty(shown);
    }

    [Fact]
    public void A_text_that_is_not_showing_is_new()
    {
        DebugEntries entries = new();

        bool isNew = entries.Report("Broken", Color.Red, Somewhere, seconds: 5f, now: 10, counted: false);

        Assert.True(isNew);
    }

    [Fact]
    public void A_text_already_showing_somewhere_else_is_not_new()
    {
        DebugEntries entries = new();
        entries.Report("Broken", Color.Red, Somewhere, seconds: 5f, now: 10, counted: false);

        bool isNew = entries.Report("Broken", Color.Red, Somewhere + Vector3.UnitX, seconds: 5f, now: 10, counted: false);

        Assert.False(isNew);
    }

    [Fact]
    public void A_text_that_went_away_is_new_again()
    {
        DebugEntries entries = new();
        entries.Report("Broken", Color.Red, Somewhere, seconds: 0f, now: 10, counted: false);
        Take(entries, now: 10, positioned: true);
        Take(entries, now: 11, positioned: true);

        bool isNew = entries.Report("Broken", Color.Red, Somewhere, seconds: 0f, now: 12, counted: false);

        Assert.True(isNew);
    }

    [Fact]
    public void Lines_are_taken_in_the_order_they_were_first_reported()
    {
        DebugEntries entries = new();
        entries.Report("First", Color.White, null, seconds: 5f, now: 10, counted: true);
        entries.Report("Second", Color.White, null, seconds: 5f, now: 10, counted: true);
        entries.Report("First", Color.White, null, seconds: 5f, now: 11, counted: true);

        List<DebugEntry> shown = Take(entries, now: 11, positioned: false);

        Assert.Equal(["First", "Second"], shown.Select(entry => entry.Text));
    }

    private static List<DebugEntry> Take(DebugEntries entries, double now, bool positioned)
    {
        List<DebugEntry> shown = [];
        entries.Take(now, positioned, shown);

        return shown;
    }
}

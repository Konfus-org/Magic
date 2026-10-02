using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class SlotsTests
{
    [Fact]
    public void An_added_value_is_in_the_slot_it_was_given()
    {
        Slots<string> slots = new();

        uint slot = slots.Add("first");

        Assert.Equal("first", slots[slot]);
    }

    [Fact]
    public void Values_get_slots_of_their_own()
    {
        Slots<string> slots = new();

        uint first = slots.Add("first");
        uint second = slots.Add("second");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_removed_slot_is_empty()
    {
        Slots<string> slots = new();
        uint slot = slots.Add("first");

        slots.Remove(slot);

        Assert.Null(slots[slot]);
    }

    [Fact]
    public void A_removed_slot_is_given_out_again()
    {
        Slots<string> slots = new();
        uint removed = slots.Add("first");
        slots.Add("second");
        slots.Remove(removed);

        uint reused = slots.Add("third");

        Assert.Equal(removed, reused);
    }

    [Fact]
    public void Removing_keeps_the_other_slots_where_they_are()
    {
        Slots<string> slots = new();
        uint removed = slots.Add("first");
        uint kept = slots.Add("second");

        slots.Remove(removed);

        Assert.Equal("second", slots[kept]);
    }

    [Fact]
    public void Removing_lowers_the_used_count_and_not_the_count()
    {
        Slots<string> slots = new();
        uint removed = slots.Add("first");
        slots.Add("second");

        slots.Remove(removed);

        Assert.Equal((1, 2), (slots.Used, slots.Count));
    }
}

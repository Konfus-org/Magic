using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Xunit;

namespace Magic.UnitTests.Components;

public sealed class PassListTests
{
    [Fact]
    public void The_list_ends_at_the_first_empty_handle()
    {
        PassList list = default;
        list[0] = new Handle<Pass>(7);
        list[2] = new Handle<Pass>(9);

        int count = list.Count;

        Assert.Equal(1, count);
    }

    [Fact]
    public void A_full_list_counts_every_slot()
    {
        PassList list = default;
        for (int i = 0; i < PassList.Capacity; i++)
            list[i] = new Handle<Pass>((ulong)i + 1);

        int count = list.Count;

        Assert.Equal(PassList.Capacity, count);
    }
}

using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class RefCountTableTests
{
    [Fact]
    public void A_missing_key_is_not_acquired()
    {
        RefCountTable<ulong, string> table = new();

        bool acquired = table.TryAcquire(1, out _);

        Assert.False(acquired);
    }

    [Fact]
    public void Acquiring_an_added_key_hands_back_its_value()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");

        table.TryAcquire(1, out string value);

        Assert.Equal("one", value);
    }

    [Fact]
    public void Acquiring_takes_another_reference()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");

        table.TryAcquire(1, out _);

        Assert.Equal(2, table.RefsOf(1));
    }

    [Fact]
    public void Releasing_with_references_left_keeps_the_entry()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");
        table.TryAcquire(1, out _);

        bool last = table.Release(1, out _);

        Assert.False(last);
    }

    [Fact]
    public void Releasing_the_last_reference_hands_back_the_value()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");

        table.Release(1, out string value);

        Assert.Equal("one", value);
    }

    [Fact]
    public void Releasing_the_last_reference_removes_the_entry()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");

        table.Release(1, out _);

        Assert.False(table.Contains(1));
    }

    [Fact]
    public void Releasing_a_missing_key_is_not_a_last_reference()
    {
        RefCountTable<ulong, string> table = new();

        bool last = table.Release(7, out _);

        Assert.False(last);
    }

    [Fact]
    public void Set_replaces_the_value()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");

        table.Set(1, "uno");

        table.TryGet(1, out string value);
        Assert.Equal("uno", value);
    }

    [Fact]
    public void Set_keeps_the_references()
    {
        RefCountTable<ulong, string> table = new();
        table.Add(1, "one");
        table.TryAcquire(1, out _);

        table.Set(1, "uno");

        Assert.Equal(2, table.RefsOf(1));
    }
}

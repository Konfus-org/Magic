using Magic.Contexts.Assets;
using Magic.Contexts.Components;

namespace Magic.Tests.Contexts.Components;

public sealed class ScriptsTests
{
    [Fact]
    public void Adding_a_script_makes_it_held()
    {
        Scripts scripts = default;
        Handle<Script> spin = new(3364);

        scripts.Add(spin);

        Assert.True(scripts.Has(spin));
    }

    [Fact]
    public void Adding_a_script_says_it_was_added()
    {
        Scripts scripts = default;

        bool added = scripts.Add(new Handle<Script>(3364));

        Assert.True(added);
    }

    [Fact]
    public void Adding_the_same_script_again_is_refused()
    {
        Scripts scripts = default;
        Handle<Script> spin = new(3364);
        scripts.Add(spin);

        bool added = scripts.Add(spin);

        Assert.False(added);
    }

    [Fact]
    public void Adding_none_is_refused()
    {
        Scripts scripts = default;

        bool added = scripts.Add(Handle<Script>.None);

        Assert.False(added);
    }

    [Fact]
    public void Adding_the_last_script_the_capacity_allows_is_accepted()
    {
        // Every room but one filled.
        Scripts scripts = default;
        for (int i = 1; i < Scripts.Capacity; i++)
        {
            scripts.Add(new Handle<Script>((ulong)i));
        }

        bool added = scripts.Add(new Handle<Script>(100));

        Assert.True(added);
    }

    [Fact]
    public void Adding_past_the_capacity_is_refused()
    {
        Scripts scripts = default;
        for (int i = 1; i <= Scripts.Capacity; i++)
        {
            scripts.Add(new Handle<Script>((ulong)i));
        }

        bool added = scripts.Add(new Handle<Script>(100));

        Assert.False(added);
    }

    [Fact]
    public void Removing_a_script_makes_it_not_held()
    {
        Scripts scripts = default;
        Handle<Script> spin = new(3364);
        scripts.Add(spin);

        scripts.Remove(spin);

        Assert.False(scripts.Has(spin));
    }

    [Fact]
    public void Removing_a_script_frees_its_room()
    {
        Scripts scripts = default;
        for (int i = 1; i <= Scripts.Capacity; i++)
        {
            scripts.Add(new Handle<Script>((ulong)i));
        }

        scripts.Remove(new Handle<Script>(3));

        Assert.True(scripts.Add(new Handle<Script>(100)));
    }

    [Fact]
    public void Removing_a_script_not_held_says_so()
    {
        Scripts scripts = default;

        bool removed = scripts.Remove(new Handle<Script>(3364));

        Assert.False(removed);
    }

    [Fact]
    public void None_is_never_held()
    {
        Scripts scripts = default;

        bool has = scripts.Has(Handle<Script>.None);

        Assert.False(has);
    }

    [Fact]
    public void The_same_scripts_in_another_order_are_equal()
    {
        Scripts spinThenHover = default;
        spinThenHover.Add(new Handle<Script>(1));
        spinThenHover.Add(new Handle<Script>(2));
        Scripts hoverThenSpin = default;
        hoverThenSpin.Add(new Handle<Script>(2));
        hoverThenSpin.Add(new Handle<Script>(1));

        bool equal = spinThenHover == hoverThenSpin;

        Assert.True(equal);
    }

    [Fact]
    public void A_part_of_the_scripts_is_not_equal_to_all_of_them()
    {
        Scripts spin = default;
        spin.Add(new Handle<Script>(1));
        Scripts spinAndHover = default;
        spinAndHover.Add(new Handle<Script>(1));
        spinAndHover.Add(new Handle<Script>(2));

        bool equal = spin == spinAndHover;

        Assert.False(equal);
    }

    [Fact]
    public void Equal_scripts_hash_the_same()
    {
        Scripts spinThenHover = default;
        spinThenHover.Add(new Handle<Script>(1));
        spinThenHover.Add(new Handle<Script>(2));
        Scripts hoverThenSpin = default;
        hoverThenSpin.Add(new Handle<Script>(2));
        hoverThenSpin.Add(new Handle<Script>(1));

        int hash = spinThenHover.GetHashCode();

        Assert.Equal(hoverThenSpin.GetHashCode(), hash);
    }
}

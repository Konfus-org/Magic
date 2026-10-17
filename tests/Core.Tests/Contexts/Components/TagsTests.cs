using Magic.Contexts.Components;
using Magic.Contexts.ECS;

namespace Magic.Tests.Contexts.Components;

public sealed class TagsTests
{
    [Fact]
    public void Adding_a_tag_makes_it_held()
    {
        Tags tags = default;
        Tag door = Tag.Of("door");

        tags.Add(door);

        Assert.True(tags.Has(door));
    }

    [Fact]
    public void Adding_a_tag_says_it_was_added()
    {
        Tags tags = default;

        bool added = tags.Add(Tag.Of("door"));

        Assert.True(added);
    }

    [Fact]
    public void Adding_the_same_tag_again_is_refused()
    {
        Tags tags = default;
        Tag door = Tag.Of("door");
        tags.Add(door);

        bool added = tags.Add(door);

        Assert.False(added);
    }

    [Fact]
    public void Adding_none_is_refused()
    {
        Tags tags = default;

        bool added = tags.Add(default);

        Assert.False(added);
    }

    [Fact]
    public void Adding_the_last_tag_the_capacity_allows_is_accepted()
    {
        Tags tags = default;
        for (int i = 1; i < Tags.Capacity; i++)
        {
            tags.Add(Tag.Of($"tag{i}"));
        }

        bool added = tags.Add(Tag.Of("one more"));

        Assert.True(added);
    }

    [Fact]
    public void Adding_past_the_capacity_is_refused()
    {
        Tags tags = default;
        for (int i = 0; i < Tags.Capacity; i++)
        {
            tags.Add(Tag.Of($"tag{i}"));
        }

        bool added = tags.Add(Tag.Of("one more"));

        Assert.False(added);
    }

    [Fact]
    public void A_tag_not_added_is_not_held()
    {
        Tags tags = default;
        tags.Add(Tag.Of("door"));

        bool has = tags.Has(Tag.Of("window"));

        Assert.False(has);
    }

    [Fact]
    public void None_is_never_held()
    {
        Tags tags = default;

        bool has = tags.Has(default);

        Assert.False(has);
    }

    [Fact]
    public void The_same_tags_in_another_order_are_equal()
    {
        Tags doorThenWindow = default;
        doorThenWindow.Add(Tag.Of("door"));
        doorThenWindow.Add(Tag.Of("window"));
        Tags windowThenDoor = default;
        windowThenDoor.Add(Tag.Of("window"));
        windowThenDoor.Add(Tag.Of("door"));

        bool equal = doorThenWindow == windowThenDoor;

        Assert.True(equal);
    }

    [Fact]
    public void Different_tags_are_not_equal()
    {
        Tags door = default;
        door.Add(Tag.Of("door"));
        Tags window = default;
        window.Add(Tag.Of("window"));

        bool equal = door == window;

        Assert.False(equal);
    }

    [Fact]
    public void A_part_of_the_tags_is_not_equal_to_all_of_them()
    {
        Tags door = default;
        door.Add(Tag.Of("door"));
        Tags doorAndWindow = default;
        doorAndWindow.Add(Tag.Of("door"));
        doorAndWindow.Add(Tag.Of("window"));

        bool equal = door == doorAndWindow;

        Assert.False(equal);
    }

    [Fact]
    public void Equal_tags_hash_the_same()
    {
        Tags doorThenWindow = default;
        doorThenWindow.Add(Tag.Of("door"));
        doorThenWindow.Add(Tag.Of("window"));
        Tags windowThenDoor = default;
        windowThenDoor.Add(Tag.Of("window"));
        windowThenDoor.Add(Tag.Of("door"));

        int hash = doorThenWindow.GetHashCode();

        Assert.Equal(windowThenDoor.GetHashCode(), hash);
    }
}

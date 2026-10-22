using Magic.Contexts.ECS;

namespace Magic.Tests.Contexts.ECS;

public sealed class TagTests
{
    [Fact]
    public void The_same_name_makes_the_same_tag()
    {
        Tag first = Tag.Of("door");

        Tag second = Tag.Of("door");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_names_make_different_tags()
    {
        Tag door = Tag.Of("door");

        Tag window = Tag.Of("window");

        Assert.NotEqual(door, window);
    }

    [Fact]
    public void Names_differing_in_case_make_different_tags()
    {
        Tag lower = Tag.Of("door");

        Tag upper = Tag.Of("Door");

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void An_empty_name_is_refused() => Assert.Throws<ArgumentException>(() => Tag.Of(string.Empty));

    [Fact]
    public void A_tag_made_from_a_name_is_valid()
    {
        Tag tag = Tag.Of("door");

        Assert.True(tag.IsValid);
    }
}

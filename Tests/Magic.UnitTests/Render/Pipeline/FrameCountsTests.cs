using DeferredRendererGem;
using System.Drawing;
using Xunit;

namespace Magic.UnitTests.Render.Pipeline;

public sealed class FrameCountsTests
{
    [Fact]
    public void A_count_resolves_to_what_it_was_built_with()
    {
        FrameCounts counts = default(FrameCounts) with { PageCount = 7 };

        bool resolved = counts.TryResolve("$pageCount", out uint value);

        Assert.True(resolved);
        Assert.Equal(7u, value);
    }

    [Fact]
    public void A_name_that_is_not_a_count_does_not_resolve()
    {
        FrameCounts counts = default;

        bool resolved = counts.TryResolve("$nothing", out _);

        Assert.False(resolved);
    }

    [Fact]
    public void Narrowing_to_a_view_sets_its_size()
    {
        FrameCounts counts = default;

        FrameCounts narrowed = counts.WithView(new Rectangle(10, 20, 300, 200), 0);

        Assert.Equal((300u, 200u), (narrowed.ViewWidth, narrowed.ViewHeight));
    }

    [Fact]
    public void Every_listed_name_is_one()
    {
        FrameCounts counts = default;

        bool[] known = [.. FrameCounts.Names.Select(name => counts.TryResolve(name, out _))];

        Assert.All(known, Assert.True);
    }
}

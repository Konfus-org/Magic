using DeferredRendererGem;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class GiBricksTests
{
    [Fact]
    public void A_new_mesh_queues_its_brick_to_build()
    {
        GiBricks bricks = new();

        uint brick = bricks.Acquire(5);

        Assert.NotEqual(GiBricks.None, brick);
        Assert.Equal(5u, bricks.Pending.Dequeue());
    }

    [Fact]
    public void A_mesh_asked_for_again_keeps_its_brick()
    {
        GiBricks bricks = new();

        uint first = bricks.Acquire(5);
        uint second = bricks.Acquire(5);

        Assert.Equal(first, second);
        Assert.Single(bricks.Pending);
    }

    [Fact]
    public void A_freed_brick_is_used_again()
    {
        GiBricks bricks = new();
        uint brick = bricks.Acquire(5);

        bricks.ReleaseMesh(5);
        uint next = bricks.Acquire(6);

        Assert.Equal(brick, next);
    }

    [Fact]
    public void Two_meshes_get_different_bricks()
    {
        GiBricks bricks = new();

        Assert.NotEqual(bricks.Acquire(1), bricks.Acquire(2));
    }
}

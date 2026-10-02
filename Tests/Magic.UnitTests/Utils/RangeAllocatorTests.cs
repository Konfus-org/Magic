using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class RangeAllocatorTests
{
    [Fact]
    public void Allocations_do_not_overlap()
    {
        RangeAllocator allocator = new(1024, 64);

        RangeAllocator.Allocation first = allocator.Allocate(100);
        RangeAllocator.Allocation second = allocator.Allocate(200);

        Assert.True(first.Offset + 100 <= second.Offset || second.Offset + 200 <= first.Offset);
    }

    [Fact]
    public void Allocating_takes_the_size_from_free_storage()
    {
        RangeAllocator allocator = new(1024, 64);

        allocator.Allocate(100);

        Assert.Equal(1024u - 100u, allocator.FreeStorage);
    }

    [Fact]
    public void Freeing_returns_the_size_to_free_storage()
    {
        RangeAllocator allocator = new(1024, 64);
        RangeAllocator.Allocation first = allocator.Allocate(100);

        allocator.Free(first);

        Assert.Equal(1024u, allocator.FreeStorage);
    }

    [Fact]
    public void A_freed_hole_is_reused()
    {
        RangeAllocator allocator = new(1024, 64);
        allocator.Allocate(100);
        RangeAllocator.Allocation hole = allocator.Allocate(200);
        allocator.Allocate(300);
        allocator.Free(hole);

        RangeAllocator.Allocation reused = allocator.Allocate(150);

        Assert.Equal(hole.Offset, reused.Offset);
    }

    [Fact]
    public void Freed_neighbours_merge_back_into_one_range()
    {
        RangeAllocator allocator = new(1024, 64);
        RangeAllocator.Allocation first = allocator.Allocate(100);
        RangeAllocator.Allocation second = allocator.Allocate(200);
        RangeAllocator.Allocation third = allocator.Allocate(300);
        allocator.Free(second);
        allocator.Free(first);
        allocator.Free(third);

        RangeAllocator.Allocation whole = allocator.Allocate(1024);

        Assert.False(whole.IsNone);
    }

    [Fact]
    public void The_whole_capacity_can_be_allocated_at_once()
    {
        RangeAllocator allocator = new(256, 16);

        RangeAllocator.Allocation whole = allocator.Allocate(256);

        Assert.False(whole.IsNone);
    }

    [Fact]
    public void A_full_allocator_answers_none()
    {
        RangeAllocator allocator = new(256, 16);
        allocator.Allocate(256);

        RangeAllocator.Allocation one = allocator.Allocate(1);

        Assert.True(one.IsNone);
    }

    [Fact]
    public void More_than_the_capacity_answers_none()
    {
        RangeAllocator allocator = new(256, 16);

        RangeAllocator.Allocation tooBig = allocator.Allocate(257);

        Assert.True(tooBig.IsNone);
    }

    [Fact]
    public void A_double_free_throws()
    {
        RangeAllocator allocator = new(256, 16);
        RangeAllocator.Allocation first = allocator.Allocate(256);
        allocator.Free(first);

        Action again = () => allocator.Free(first);

        Assert.Throws<InvalidOperationException>(again);
    }
}

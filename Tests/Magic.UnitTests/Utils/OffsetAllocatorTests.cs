using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class OffsetAllocatorTests
{
    [Fact]
    public void Allocations_do_not_overlap()
    {
        OffsetAllocator allocator = new(1024, 64);

        OffsetAllocator.Allocation a = allocator.Allocate(100);
        OffsetAllocator.Allocation b = allocator.Allocate(200);

        Assert.True(a.Offset + 100 <= b.Offset || b.Offset + 200 <= a.Offset);
    }

    [Fact]
    public void Allocating_takes_the_size_from_free_storage()
    {
        OffsetAllocator allocator = new(1024, 64);

        allocator.Allocate(100);

        Assert.Equal(1024u - 100u, allocator.FreeStorage);
    }

    [Fact]
    public void Freeing_returns_the_size_to_free_storage()
    {
        OffsetAllocator allocator = new(1024, 64);
        OffsetAllocator.Allocation a = allocator.Allocate(100);

        allocator.Free(a);

        Assert.Equal(1024u, allocator.FreeStorage);
    }

    [Fact]
    public void A_freed_hole_is_reused()
    {
        OffsetAllocator allocator = new(1024, 64);
        allocator.Allocate(100);
        OffsetAllocator.Allocation hole = allocator.Allocate(200);
        allocator.Allocate(300);
        allocator.Free(hole);

        OffsetAllocator.Allocation reused = allocator.Allocate(150);

        Assert.Equal(hole.Offset, reused.Offset);
    }

    [Fact]
    public void Freed_neighbours_merge_back_into_one_range()
    {
        OffsetAllocator allocator = new(1024, 64);
        OffsetAllocator.Allocation a = allocator.Allocate(100);
        OffsetAllocator.Allocation b = allocator.Allocate(200);
        OffsetAllocator.Allocation c = allocator.Allocate(300);
        allocator.Free(b);
        allocator.Free(a);
        allocator.Free(c);

        OffsetAllocator.Allocation whole = allocator.Allocate(1024);

        Assert.False(whole.IsNone);
    }

    [Fact]
    public void The_whole_capacity_can_be_allocated_at_once()
    {
        OffsetAllocator allocator = new(256, 16);

        OffsetAllocator.Allocation whole = allocator.Allocate(256);

        Assert.False(whole.IsNone);
    }

    [Fact]
    public void A_full_allocator_answers_none()
    {
        OffsetAllocator allocator = new(256, 16);
        allocator.Allocate(256);

        OffsetAllocator.Allocation one = allocator.Allocate(1);

        Assert.True(one.IsNone);
    }

    [Fact]
    public void More_than_the_capacity_answers_none()
    {
        OffsetAllocator allocator = new(256, 16);

        OffsetAllocator.Allocation tooBig = allocator.Allocate(257);

        Assert.True(tooBig.IsNone);
    }

    [Fact]
    public void A_double_free_throws()
    {
        OffsetAllocator allocator = new(256, 16);
        OffsetAllocator.Allocation a = allocator.Allocate(256);
        allocator.Free(a);

        Action again = () => allocator.Free(a);

        Assert.Throws<InvalidOperationException>(again);
    }
}

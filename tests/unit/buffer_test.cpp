/**
 * @file buffer_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/collections/buffer.h.
 *
 * A buffer describes memory it does not own, so most of these run over a stack
 * array -- which is the honest shape for it. The resize tests are the exception
 * and have to allocate, because resizing is the one operation that reallocates
 * and the block has to have come from mg_alloc.
 *
 * Insert and remove are unordered here, and deliberately: a buffer's insert
 * displaces the occupant to the free slot past the end, and its remove fills
 * the gap with the last curr. Both are O(1) and neither preserves order. The
 * one ordered removal is pop_front, the queue's end, which pays O(n) to keep
 * what remains in order.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

/** @brief Counts what a buffer destroys, so the destructor calls can be asserted on. */
int g_destroyed = 0;

extern "C" void count_destruction(void* item) {
    (void)item;
    g_destroyed++;
}

class BufferTest : public testing::Test {
  protected:
    void SetUp() override {
        g_destroyed = 0;
        buffer.items = slots;
        buffer.item_size = sizeof(u32);
        buffer.count = 0;
        buffer.capacity = 8;
    }

    /** @brief Appends 1..n, so a test reads as the values it expects. */
    void fill(const u32 n) {
        for (u32 i = 1; i <= n; i++) {
            mg_buffer_push(&buffer, &i, sizeof(u32));
        }
    }

    u32 at(const ind index) {
        return *static_cast<u32*>(mg_buffer_at(&buffer, index));
    }

    u32 slots[8] = {};
    mg_buffer buffer = {};
};

TEST_F(BufferTest, AppendGrowsTheCountAndStoresACopy) {
    u32 value = 42;
    mg_buffer_push(&buffer, &value, sizeof(u32));

    EXPECT_EQ(buffer.count, 1u);
    EXPECT_EQ(at(0), 42u);

    /* A copy and not the caller's storage: changing the source afterwards must
     * not change what the buffer holds. */
    value = 99;
    EXPECT_EQ(at(0), 42u);
}

TEST_F(BufferTest, GetHandsBackTheAddressOfTheSlot) {
    fill(3);

    auto* second = static_cast<u32*>(mg_buffer_at(&buffer, 1));
    ASSERT_NE(second, nullptr);
    EXPECT_EQ(*second, 2u);

    /* Writable, which is why get does not take a const mg_buffer*. */
    *second = 20;
    EXPECT_EQ(at(1), 20u);
}

TEST_F(BufferTest, AtTypedIsTheBuffersAtWithTheSizeChecked) {
    fill(3);

    /* Same address as the untyped at, already typed: the cast lives in the
     * macro, checked against the buffer's curr size. */
    EXPECT_EQ(mg_at(&buffer, 1, u32), mg_buffer_at(&buffer, 1));

    *mg_at(&buffer, 1, u32) = 20;
    EXPECT_EQ(at(1), 20u);
}

TEST_F(BufferTest, EmptyAndFullBracketTheCapacity) {
    EXPECT_TRUE(mg_buffer_empty(&buffer));
    EXPECT_FALSE(mg_buffer_full(&buffer));

    fill(8); // the whole capacity

    EXPECT_FALSE(mg_buffer_empty(&buffer));
    EXPECT_TRUE(mg_buffer_full(&buffer));

    mg_buffer_remove(&buffer, 0);
    EXPECT_FALSE(mg_buffer_full(&buffer));
}

TEST_F(BufferTest, ContainsAnswersIdentityAndNotEquality) {
    fill(3);

    EXPECT_TRUE(mg_buffer_contains(&buffer, mg_buffer_at(&buffer, 0)));
    EXPECT_TRUE(mg_buffer_contains(&buffer, mg_buffer_at(&buffer, 2)));

    /* The address of a slot past the count is not one of the live items, even
     * though it is inside the allocation. */
    EXPECT_FALSE(mg_buffer_contains(&buffer, &slots[3]));

    /* An equal value stored somewhere else is not the same curr. */
    u32 elsewhere = 1;
    EXPECT_FALSE(mg_buffer_contains(&buffer, &elsewhere));
}

TEST_F(BufferTest, InsertDisplacesTheOccupantToTheEnd) {
    fill(3); // 1 2 3
    u32 value = 99;
    mg_buffer_insert(&buffer, 0, &value, sizeof(u32));

    EXPECT_EQ(buffer.count, 4u);
    EXPECT_EQ(at(0), 99u);
    /* 1 was at index 0 and is now at the end, not shifted right by one. */
    EXPECT_EQ(at(1), 2u);
    EXPECT_EQ(at(2), 3u);
    EXPECT_EQ(at(3), 1u);
}

TEST_F(BufferTest, RemoveFillsTheGapWithTheLast) {
    fill(4); // 1 2 3 4
    mg_buffer_remove(&buffer, 0);

    EXPECT_EQ(buffer.count, 3u);
    EXPECT_EQ(at(0), 4u);
    EXPECT_EQ(at(1), 2u);
    EXPECT_EQ(at(2), 3u);
}

TEST_F(BufferTest, RemovingTheLastItemJustShortensTheBuffer) {
    /* The case where the gap and the filler are the same slot. Copying a region
     * onto itself is undefined, so the copy has to be skipped -- and the curr
     * before it must not be disturbed. */
    fill(2);
    mg_buffer_remove(&buffer, 1);

    EXPECT_EQ(buffer.count, 1u);
    EXPECT_EQ(at(0), 1u);
}

TEST_F(BufferTest, RemoveRunsTheStoredDestructorOnce) {
    buffer.destructor = count_destruction;
    fill(3);
    mg_buffer_remove(&buffer, 1);
    EXPECT_EQ(g_destroyed, 1);
}

TEST_F(BufferTest, ClearDestroysEveryItemAndEmptiesTheBuffer) {
    buffer.destructor = count_destruction;
    fill(5);
    mg_buffer_clear(&buffer);

    EXPECT_EQ(buffer.count, 0u);
    /* Every one of them, not just the first: the count is zeroed after the
     * walk, because FOREACH re-reads it every iteration. */
    EXPECT_EQ(g_destroyed, 5);
}

TEST_F(BufferTest, ClearWithNoDestructorIsStillAClear) {
    fill(5);
    mg_buffer_clear(&buffer);
    EXPECT_EQ(buffer.count, 0u);
    EXPECT_EQ(g_destroyed, 0);
}

TEST(BufferFree, FreeDestroysTheLiveItemsBeforeReleasingTheStorage) {
    g_destroyed = 0;

    mg_buffer buffer =
        mg_buffer_new(sizeof(u32), 4, count_destruction, MG_MEMORY_TAG_UNKNOWN);
    ASSERT_NE(buffer.items, nullptr);
    for (u32 i = 1; i <= 3; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    mg_buffer_free(&buffer, MG_MEMORY_TAG_UNKNOWN);

    /* The stored destructor ran over every live curr, with no destructor
     * passed at the free site. */
    EXPECT_EQ(g_destroyed, 3);
    EXPECT_EQ(buffer.items, nullptr);
    EXPECT_EQ(buffer.count, 0u);
}

TEST_F(BufferTest, FirstAndLastAndIndex) {
    EXPECT_EQ(mg_first(&buffer), nullptr);
    EXPECT_EQ(mg_last(&buffer), nullptr);

    fill(3);

    EXPECT_EQ(*static_cast<u32*>(mg_first(&buffer)), 1u);
    EXPECT_EQ(*static_cast<u32*>(mg_last(&buffer)), 3u);
    EXPECT_EQ(mg_buffer_index_of(&buffer, mg_last(&buffer), sizeof(u32)), 2u);
}

TEST_F(BufferTest, ForeachWalksEveryItemInOrder) {
    fill(4);

    u32 sum = 0;
    u32 seen = 0;
    u32* item;
    mg_foreach(item, &buffer) {
        sum += *item;
        seen++;
    }

    EXPECT_EQ(seen, 4u);
    EXPECT_EQ(sum, 10u);
}

TEST_F(BufferTest, ForeachOverAnEmptyBufferRunsZeroTimes) {
    u32 seen = 0;
    u32* item;
    mg_foreach(item, &buffer) {
        (void)item;
        seen++;
    }
    EXPECT_EQ(seen, 0u);
}

TEST_F(BufferTest, WhereSkipsNonMatchesRatherThanStoppingAtThem) {
    /* The bug this macro was rewritten for: with the condition in the loop's
     * own test, a non-matching curr ends the walk, so a filter over 1 2 3 4
     * where the match is "even" would see nothing at all -- the first curr
     * fails. It has to see 2 and 4. */
    fill(4);

    u32 sum = 0;
    u32 seen = 0;
    u32* item;
    mg_where(item, &buffer, *item % 2 == 0) {
        sum += *item;
        seen++;
    }

    EXPECT_EQ(seen, 2u);
    EXPECT_EQ(sum, 6u);
}

TEST_F(BufferTest, ViewOfABufferSpansItsLiveItems) {
    fill(3);

    const mg_view view = mg_buffer_view(&buffer);

    /* A borrow of the same slots, clipped to the live count. */
    EXPECT_EQ(view.items, buffer.items);
    EXPECT_EQ(view.item_size, sizeof(u32));
    EXPECT_EQ(view.count, 3u);
}

TEST_F(BufferTest, IterNextHandsBackEachItemInOrder) {
    fill(3);

    mg_iter it = mg_iter_construct(mg_buffer_view(&buffer));
    EXPECT_EQ(it.curr, nullptr) << "no curr before the first next";

    u32 seen = 0;
    u32 sum = 0;
    const u32* item;
    while ((item = static_cast<const u32*>(mg_iter_next(&it))) != nullptr) {
        EXPECT_EQ(item, it.curr) << "the return and it.curr are the same address";
        sum += *item;
        EXPECT_EQ(it.index, seen);
        seen++;
    }

    EXPECT_EQ(seen, 3u);
    EXPECT_EQ(sum, 6u);
    EXPECT_EQ(it.curr, nullptr) << "the walk ends with curr back at NULL";
}

TEST_F(BufferTest, ReverseIterAndPreviousWalkLastToFirst) {
    fill(3); // 1 2 3

    mg_iter it = mg_iter_construct_reverse(mg_buffer_view(&buffer));

    EXPECT_EQ(mg_iter_next(&it), nullptr) << "nothing ahead of the end";

    ASSERT_NE(mg_iter_previous(&it), nullptr);
    EXPECT_EQ(*static_cast<const u32*>(it.curr), 3u);
    EXPECT_EQ(it.index, 2u);

    ASSERT_NE(mg_iter_previous(&it), nullptr);
    EXPECT_EQ(*static_cast<const u32*>(it.curr), 2u);

    ASSERT_NE(mg_iter_previous(&it), nullptr);
    EXPECT_EQ(*static_cast<const u32*>(it.curr), 1u);
    EXPECT_EQ(it.index, 0u);

    EXPECT_EQ(mg_iter_previous(&it), nullptr);
    EXPECT_EQ(it.curr, nullptr);
}

TEST_F(BufferTest, NextAndPreviousUndoEachOther) {
    fill(3); // 1 2 3

    /* The cursor stands between items: next hands the curr ahead and steps
     * past it, previous the curr behind, so the pair lands on the same curr. */
    mg_iter it = mg_iter_construct(mg_buffer_view(&buffer));

    EXPECT_EQ(*static_cast<const u32*>(mg_iter_next(&it)), 1u);
    EXPECT_EQ(*static_cast<const u32*>(mg_iter_next(&it)), 2u);
    EXPECT_EQ(*static_cast<const u32*>(mg_iter_previous(&it)), 2u);
    EXPECT_EQ(*static_cast<const u32*>(mg_iter_next(&it)), 2u);
}

TEST_F(BufferTest, IterOverAnEmptyViewEndsImmediately) {
    mg_iter it = mg_iter_construct(mg_buffer_view(&buffer));

    EXPECT_EQ(mg_iter_next(&it), nullptr);
    EXPECT_EQ(mg_iter_previous(&it), nullptr);
    EXPECT_EQ(it.curr, nullptr);
}

TEST(BufferReserve, DoublesFromTheBuffersOwnCapacityWithNoFloor) {
    /* 0 -> 1 -> 2 -> 4: the buffer has no opinion about a minimum. The 8-slot
     * start a list gets is list.h asking for 8, not this function providing it. */
    mg_buffer buffer = {nullptr, sizeof(u32), 0, 0, nullptr};

    ASSERT_TRUE(mg_buffer_reserve(&buffer, 1, MG_MEMORY_TAG_UNKNOWN));
    EXPECT_EQ(buffer.capacity, 1u);

    ASSERT_TRUE(mg_buffer_reserve(&buffer, 2, MG_MEMORY_TAG_UNKNOWN));
    EXPECT_EQ(buffer.capacity, 2u);

    ASSERT_TRUE(mg_buffer_reserve(&buffer, 3, MG_MEMORY_TAG_UNKNOWN));
    EXPECT_EQ(buffer.capacity, 4u);

    /* And it keeps doubling until the ask fits, not once per call. */
    ASSERT_TRUE(mg_buffer_reserve(&buffer, 100, MG_MEMORY_TAG_UNKNOWN));
    EXPECT_EQ(buffer.capacity, 128u);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferReserve, DoesNothingWhenTheAskAlreadyFits) {
    mg_buffer buffer = {nullptr, sizeof(u32), 0, 0, nullptr};
    ASSERT_TRUE(mg_buffer_reserve(&buffer, 64, MG_MEMORY_TAG_UNKNOWN));
    void* items = buffer.items;

    /* Never shrinks, never reallocates without need. */
    ASSERT_TRUE(mg_buffer_reserve(&buffer, 3, MG_MEMORY_TAG_UNKNOWN));
    EXPECT_EQ(buffer.capacity, 64u);
    EXPECT_EQ(buffer.items, items);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferReserve, KeepsTheItemsAcrossAGrowth) {
    mg_buffer buffer = {nullptr, sizeof(u32), 0, 0, nullptr};
    ASSERT_TRUE(mg_buffer_reserve(&buffer, 2, MG_MEMORY_TAG_UNKNOWN));
    for (u32 i = 1; i <= 2; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    ASSERT_TRUE(mg_buffer_reserve(&buffer, 100, MG_MEMORY_TAG_UNKNOWN));

    EXPECT_EQ(buffer.count, 2u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 0)), 1u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 1)), 2u);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferReserve, RefusesAnImpossibleAskAndLeavesTheBufferAlone) {
    /* A needed count whose byte size cannot exist: the doubling loop must not
     * wrap and spin, and the refusal must leave the buffer untouched --
     * mg_resize returns NULL with the old block still valid. */
    mg_buffer buffer = {nullptr, sizeof(u32), 0, 0, nullptr};
    ASSERT_TRUE(mg_buffer_reserve(&buffer, 4, MG_MEMORY_TAG_UNKNOWN));
    void* items = buffer.items;

    EXPECT_FALSE(mg_buffer_reserve(&buffer, U64_MAX, MG_MEMORY_TAG_UNKNOWN));

    EXPECT_EQ(buffer.items, items);
    EXPECT_EQ(buffer.capacity, 4u);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferResize, GrowsAndKeepsTheItems) {
    mg_buffer buffer = {};
    buffer.items = mg_alloc(2, sizeof(u32), MG_MEMORY_TAG_UNKNOWN);
    buffer.item_size = sizeof(u32);
    buffer.capacity = 2;
    ASSERT_NE(buffer.items, nullptr);

    for (u32 i = 1; i <= 2; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    mg_buffer_resize(&buffer, 8, MG_MEMORY_TAG_UNKNOWN);

    EXPECT_EQ(buffer.capacity, 8u);
    EXPECT_EQ(buffer.count, 2u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 0)), 1u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 1)), 2u);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferResize, ShrinkingDestroysWhatNoLongerFits) {
    g_destroyed = 0;

    mg_buffer buffer = {};
    buffer.items = mg_alloc(4, sizeof(u32), MG_MEMORY_TAG_UNKNOWN);
    buffer.item_size = sizeof(u32);
    buffer.capacity = 4;
    buffer.destructor = count_destruction;
    ASSERT_NE(buffer.items, nullptr);

    for (u32 i = 1; i <= 4; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    mg_buffer_resize(&buffer, 2, MG_MEMORY_TAG_UNKNOWN);

    /* The two that fell outside the new range, and only those. The slots
     * between count and capacity never held an curr, so handing one to a
     * destructor would be asking it to release fields nobody set. */
    EXPECT_EQ(g_destroyed, 2);
    EXPECT_EQ(buffer.count, 2u);
    /* And the capacity actually moved, which the old shrink path never did. */
    EXPECT_EQ(buffer.capacity, 2u);

    mg_free(buffer.items, MG_MEMORY_TAG_UNKNOWN);
}

TEST(BufferResize, ResizingToNothingReleasesTheAllocation) {
    mg_buffer buffer = {};
    buffer.items = mg_alloc(4, sizeof(u32), MG_MEMORY_TAG_UNKNOWN);
    buffer.item_size = sizeof(u32);
    buffer.capacity = 4;
    ASSERT_NE(buffer.items, nullptr);

    u32 value = 1;
    mg_buffer_push(&buffer, &value, sizeof(u32));

    mg_buffer_resize(&buffer, 0, MG_MEMORY_TAG_UNKNOWN);

    /* A free, spelled as one. Falling through to realloc(ptr, 0) would leave
     * the buffer claiming a capacity it no longer has. */
    EXPECT_EQ(buffer.items, nullptr);
    EXPECT_EQ(buffer.count, 0u);
    EXPECT_EQ(buffer.capacity, 0u);
}

TEST(BufferResize, ResizingToTheSameCapacityDoesNothing) {
    u32 slots[4] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 4, nullptr};

    mg_buffer_resize(&buffer, 4, MG_MEMORY_TAG_UNKNOWN);

    EXPECT_EQ(buffer.items, slots);
    EXPECT_EQ(buffer.capacity, 4u);
}

TEST(BufferPopFront, HandsBackTheOldestAndKeepsTheRestInOrder) {
    u32 slots[4] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 4, nullptr};
    for (u32 i = 1; i <= 4; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32)); // 1 2 3 4
    }

    u32 popped = 0;
    ASSERT_TRUE(mg_buffer_pop_front(&buffer, &popped));

    /* FIFO: the first pushed comes out first, and unlike the swapping
     * remove, what remains keeps its order. */
    EXPECT_EQ(popped, 1u);
    EXPECT_EQ(buffer.count, 3u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 0)), 2u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 1)), 3u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, 2)), 4u);
    /* The vacated end slot was scrubbed, not left holding a stale 4. */
    EXPECT_EQ(slots[3], 0u);
}

TEST(BufferPopFront, DrainsInPushOrderAndThenRefuses) {
    u32 slots[2] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 2, nullptr};
    for (u32 i = 1; i <= 2; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    u32 popped = 0;
    ASSERT_TRUE(mg_buffer_pop_front(&buffer, &popped));
    EXPECT_EQ(popped, 1u);
    ASSERT_TRUE(mg_buffer_pop_front(&buffer, &popped));
    EXPECT_EQ(popped, 2u);

    EXPECT_FALSE(mg_buffer_pop_front(&buffer, &popped));
    EXPECT_EQ(popped, 2u) << "the refusal leaves out untouched";
    EXPECT_EQ(buffer.count, 0u);
}

TEST(BufferPushMany, CopiesTheWholeRunOntoTheEnd) {
    u32 slots[8] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 8, nullptr};
    u32 head = 1;
    mg_buffer_push(&buffer, &head, sizeof(u32));

    const u32 tail[3] = {2, 3, 4};
    mg_buffer_push_many(&buffer, tail, 3);

    EXPECT_EQ(buffer.count, 4u);
    for (u32 i = 0; i < 4; i++) {
        EXPECT_EQ(*static_cast<u32*>(mg_buffer_at(&buffer, i)), i + 1) << "slot " << i;
    }
}

TEST(BufferPushMany, ZeroItemsIsANoOpEvenFromNull) {
    u32 slots[2] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 2, nullptr};

    mg_buffer_push_many(&buffer, nullptr, 0);

    EXPECT_EQ(buffer.count, 0u);
}

TEST(BufferPopPeek, PeekIsTheLastItemAndRemovesNothing) {
    u32 slots[4] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 4, nullptr};
    for (u32 i = 1; i <= 3; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    EXPECT_EQ(*static_cast<u32*>(mg_buffer_peek(&buffer)), 3u);
    EXPECT_EQ(buffer.count, 3u);
}

TEST(BufferPopPeek, PopCopiesOutWhatPeekShowedAndScrubsTheSlot) {
    u32 slots[4] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 4, nullptr};
    for (u32 i = 1; i <= 3; i++) {
        mg_buffer_push(&buffer, &i, sizeof(u32));
    }

    /* The popped curr is the popper's now, copied out whole; the slot it
     * left holds zeroes, not stale bytes. */
    u32 popped = 0;
    ASSERT_TRUE(mg_buffer_pop(&buffer, &popped));
    EXPECT_EQ(popped, 3u);
    EXPECT_EQ(buffer.count, 2u);
    EXPECT_EQ(slots[2], 0u);
    EXPECT_EQ(*static_cast<u32*>(mg_buffer_peek(&buffer)), 2u);
}

TEST(BufferPopPeek, PopMovesOwnershipSoTheDestructorDoesNotRun) {
    g_destroyed = 0;

    u32 slots[2] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 2, count_destruction};
    u32 value = 7;
    mg_buffer_push(&buffer, &value, sizeof(u32));

    /* Pop gives the curr over; destroying it too would hand the popper a
     * copy whose innards were already released. Remove destroys, pop moves. */
    u32 popped = 0;
    ASSERT_TRUE(mg_buffer_pop(&buffer, &popped));
    EXPECT_EQ(popped, 7u);
    EXPECT_EQ(g_destroyed, 0);
}

TEST(BufferPopPeek, TheEmptyBufferPeeksNullAndPopsFalse) {
    u32 slots[2] = {};
    mg_buffer buffer = {slots, sizeof(u32), 0, 2, nullptr};

    u32 popped = 42;
    EXPECT_EQ(mg_buffer_peek(&buffer), nullptr);
    EXPECT_FALSE(mg_buffer_pop(&buffer, &popped));
    EXPECT_EQ(popped, 42u) << "the refusal leaves out untouched";
    EXPECT_EQ(buffer.count, 0u);
}

} // namespace

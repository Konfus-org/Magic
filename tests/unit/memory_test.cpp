/**
 * @file memory_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/memory/alloc.h, magic/memory/stats.h and magic/memory/copy.h.
 *
 * The floor the whole container layer stands on: every container allocates
 * through mg_alloc and shifts its items with mg_mem_move, so a defect
 * here shows up as a container test failing for a reason that has nothing to do
 * with containers. These run first for that reason.
 *
 * Nothing here calls a function in a way its asserts reject -- a null
 * destination, a zero count. MG_ASSERT traps, and a trap is a dead test
 * process rather than a failed assertion, so the preconditions are left to the
 * build that compiles them in and the tests cover the paths that report
 * failure by returning it.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

TEST(Alloc, ReturnsZeroedMemory) {
    auto* bytes = static_cast<byte*>(mg_alloc(64, sizeof(byte), MG_MEMORY_TAG_UNKNOWN));
    ASSERT_NE(bytes, nullptr);

    for (len i = 0; i < 64; i++) {
        EXPECT_EQ(bytes[i], 0) << "byte " << i << " came back non-zero";
    }

    mg_free(bytes, MG_MEMORY_TAG_UNKNOWN);
}

TEST(Alloc, MacrosAllocateTheRightShape) {
    u32* one = mg_alloc_one(u32, MG_MEMORY_TAG_UNKNOWN);
    ASSERT_NE(one, nullptr);
    EXPECT_EQ(*one, 0u);
    mg_free(one, MG_MEMORY_TAG_UNKNOWN);

    u32* many = mg_alloc_buffer(u32, 4, MG_MEMORY_TAG_UNKNOWN);
    ASSERT_NE(many, nullptr);
    many[3] = 7;
    EXPECT_EQ(many[3], 7u);
    mg_free(many, MG_MEMORY_TAG_UNKNOWN);
}

TEST(Alloc, RefusesAProductThatDoesNotFitASizeT) {
    /* Neither operand is out of range on its own; their product is. A check
     * that only looked at the operands would hand back a buffer of
     * (count * size) mod 2^64 bytes, which is smaller than what was asked for
     * and is the one failure a caller cannot detect. */
    EXPECT_EQ(mg_alloc(U64_MAX, 2, MG_MEMORY_TAG_UNKNOWN), nullptr);
    EXPECT_EQ(mg_alloc(U64_MAX / 2, 4, MG_MEMORY_TAG_UNKNOWN), nullptr);
}

TEST(Alloc, ResizeKeepsWhatWasAlreadyThere) {
    auto* values = mg_alloc_buffer(u32, 4, MG_MEMORY_TAG_UNKNOWN);
    ASSERT_NE(values, nullptr);
    for (u32 i = 0; i < 4; i++) {
        values[i] = i + 1;
    }

    auto* grown =
        static_cast<u32*>(mg_resize_buffer(values, 16, sizeof(u32), MG_MEMORY_TAG_UNKNOWN));
    ASSERT_NE(grown, nullptr);

    for (u32 i = 0; i < 4; i++) {
        EXPECT_EQ(grown[i], i + 1) << "slot " << i << " did not survive the resize";
    }

    mg_free(grown, MG_MEMORY_TAG_UNKNOWN);
}

TEST(Alloc, ResizeFromNullIsAnAllocation) {
    /* What lets a container that has never allocated grow with no special case
     * at the call site. */
    auto* fresh =
        static_cast<u32*>(mg_resize_buffer(nullptr, 8, sizeof(u32), MG_MEMORY_TAG_UNKNOWN));
    ASSERT_NE(fresh, nullptr);
    mg_free(fresh, MG_MEMORY_TAG_UNKNOWN);
}

TEST(Alloc, ReclaimingNullIsANoOp) {
    mg_free(nullptr, MG_MEMORY_TAG_UNKNOWN);
    mg_free(nullptr, MG_MEMORY_TAG_UNKNOWN);
    SUCCEED();
}

/* The Stats tests below never assert an absolute figure: the ledger is
 * process-wide and cumulative, and every test in this binary allocates through
 * it. Each one snapshots the books, does its work, and checks the difference
 * -- which holds whatever ran before it. The tags used are TEXTURE, MATERIAL
 * and MODEL: nothing else in the test process allocates under them, so a delta
 * on one is attributable to the test that made it and nothing else. */

/* A build without MG_TRACK_MEMORY keeps no books at all -- the header that
 * would answer "how many bytes was this block" is not compiled in -- so every
 * figure below reads zero. Skipped rather than failed: the ledger is absent by
 * choice, not broken. */
#if MG_TRACK_MEMORY
    #define MG_NEEDS_TRACKING() ((void)0)
#else
    #define MG_NEEDS_TRACKING() GTEST_SKIP() << "MG_TRACK_MEMORY is off; there are no books to read"
#endif

TEST(Stats, AllocAndFreeCountThePayloadBytesUnderTheTag) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_TEXTURE);
    const mg_memory_stats total_before = mg_get_mem_usage().total;

    void* block = mg_alloc(16, sizeof(u32), MG_MEMORY_TAG_TEXTURE);
    ASSERT_NE(block, nullptr);

    /* Exactly the bytes asked for: the allocator's own bookkeeping header in
     * front of the block is overhead, not usage, and must not reach the books. */
    const mg_memory_stats opened = mg_get_mem_usage_for(MG_MEMORY_TAG_TEXTURE);
    EXPECT_EQ(opened.allocated - before.allocated, 16 * sizeof(u32));
    EXPECT_EQ(opened.deallocated, before.deallocated);

    mg_free(block, MG_MEMORY_TAG_TEXTURE);

    /* The free books the same figure back, so the tag's live usage --
     * allocated minus deallocated -- is what it was before the test. */
    const mg_memory_stats closed = mg_get_mem_usage_for(MG_MEMORY_TAG_TEXTURE);
    EXPECT_EQ(closed.deallocated - before.deallocated, 16 * sizeof(u32));
    EXPECT_EQ(closed.allocated - closed.deallocated, before.allocated - before.deallocated);

    /* And the total moved in step with the tag. */
    const mg_memory_stats total_after = mg_get_mem_usage().total;
    EXPECT_EQ(total_after.allocated - total_before.allocated, 16 * sizeof(u32));
    EXPECT_EQ(total_after.deallocated - total_before.deallocated, 16 * sizeof(u32));
}

TEST(Stats, ResizeBooksTheOldBytesOutAndTheNewBytesIn) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_MATERIAL);

    void* block = mg_alloc(4, sizeof(u32), MG_MEMORY_TAG_MATERIAL);
    ASSERT_NE(block, nullptr);

    void* grown = mg_resize(block, 16, sizeof(u32), MG_MEMORY_TAG_MATERIAL);
    ASSERT_NE(grown, nullptr);

    /* One resize, two entries: the old block's bytes leave and the new
     * block's arrive, so live usage is the new size and nothing lingers. */
    const mg_memory_stats resized = mg_get_mem_usage_for(MG_MEMORY_TAG_MATERIAL);
    EXPECT_EQ(resized.allocated - before.allocated, (4 + 16) * sizeof(u32));
    EXPECT_EQ(resized.deallocated - before.deallocated, 4 * sizeof(u32));

    mg_free(grown, MG_MEMORY_TAG_MATERIAL);

    /* The free books the resized figure, not the original one. */
    const mg_memory_stats closed = mg_get_mem_usage_for(MG_MEMORY_TAG_MATERIAL);
    EXPECT_EQ(closed.deallocated - before.deallocated, (4 + 16) * sizeof(u32));
    EXPECT_EQ(closed.allocated - closed.deallocated, before.allocated - before.deallocated);
}

TEST(Stats, ResizeFromNullBooksNothingOut) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_MATERIAL);

    /* NULL has no old bytes; the resize that is really an allocation must be
     * booked as one, or every list born empty would start its ledger in debt. */
    void* fresh = mg_resize(nullptr, 8, sizeof(u32), MG_MEMORY_TAG_MATERIAL);
    ASSERT_NE(fresh, nullptr);

    const mg_memory_stats opened = mg_get_mem_usage_for(MG_MEMORY_TAG_MATERIAL);
    EXPECT_EQ(opened.allocated - before.allocated, 8 * sizeof(u32));
    EXPECT_EQ(opened.deallocated, before.deallocated);

    mg_free(fresh, MG_MEMORY_TAG_MATERIAL);
}

TEST(Stats, ARefusedAllocationCountsNothing) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_MODEL);

    /* The product overflows, so nothing is handed out -- and a refusal that
     * still reached the books would show usage no free can ever balance. */
    EXPECT_EQ(mg_alloc(U64_MAX, 2, MG_MEMORY_TAG_MODEL), nullptr);

    const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_MODEL);
    EXPECT_EQ(after.allocated, before.allocated);
    EXPECT_EQ(after.deallocated, before.deallocated);
}

TEST(Stats, FreeingNullCountsNothing) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_MODEL);

    mg_free(nullptr, MG_MEMORY_TAG_MODEL);

    const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_MODEL);
    EXPECT_EQ(after.allocated, before.allocated);
    EXPECT_EQ(after.deallocated, before.deallocated);
}

TEST(Stats, TagsKeepSeparateBooks) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats other_before = mg_get_mem_usage_for(MG_MEMORY_TAG_TEXTURE);

    void* block = mg_alloc(8, sizeof(u32), MG_MEMORY_TAG_MODEL);
    ASSERT_NE(block, nullptr);
    mg_free(block, MG_MEMORY_TAG_MODEL);

    /* MODEL's business is MODEL's alone. */
    const mg_memory_stats other_after = mg_get_mem_usage_for(MG_MEMORY_TAG_TEXTURE);
    EXPECT_EQ(other_after.allocated, other_before.allocated);
    EXPECT_EQ(other_after.deallocated, other_before.deallocated);
}

TEST(Stats, TheTotalIsTheSumOfTheTags) {
    MG_NEEDS_TRACKING();
    /* True at any instant, not just around one allocation: every entry lands
     * in a tag and the total together, so the two columns can never drift. */
    const mg_memory_usage usage = mg_get_mem_usage();

    len allocated = 0;
    len deallocated = 0;
    for (len i = 0; i < MG_MEMORY_TAG_COUNT; i++) {
        allocated += usage.tagged[i].allocated;
        deallocated += usage.tagged[i].deallocated;
    }

    EXPECT_EQ(usage.total.allocated, allocated);
    EXPECT_EQ(usage.total.deallocated, deallocated);
}

TEST(Stats, TheOneTagGetterAgreesWithTheWholeLedger) {
    MG_NEEDS_TRACKING();
    const mg_memory_usage usage = mg_get_mem_usage();
    const mg_memory_stats one = mg_get_mem_usage_for(MG_MEMORY_TAG_LIST);

    EXPECT_EQ(one.allocated, usage.tagged[MG_MEMORY_TAG_LIST].allocated);
    EXPECT_EQ(one.deallocated, usage.tagged[MG_MEMORY_TAG_LIST].deallocated);
}

TEST(Stats, AnArraysWholeLifeBalancesTheArrayTag) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_ARRAY);

    mg_u32_array scores = mg_u32_array_new(16);
    ASSERT_NE(scores.items, nullptr);
    mg_u32_array_push(&scores, 7);
    mg_u32_array_free(&scores);

    /* An array allocates once, so both columns move by exactly its capacity. */
    const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_ARRAY);
    EXPECT_EQ(after.allocated - before.allocated, 16 * sizeof(u32));
    EXPECT_EQ(after.deallocated - before.deallocated, 16 * sizeof(u32));
}

TEST(Stats, AListsWholeLifeBalancesTheListTag) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_LIST);

    /* Born empty so every allocation happens as growth through the buffer
     * underneath -- the path where a tag disagreement between list and buffer
     * would put the books out of balance. Enough appends to grow more than once. */
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 0; i < 100; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }
    mg_u32_list_free(&xs);

    const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_LIST);
    EXPECT_GT(after.allocated, before.allocated);
    EXPECT_EQ(after.allocated - before.allocated, after.deallocated - before.deallocated);
}

TEST(Stats, AStringsWholeLifeBalancesTheStringTag) {
    MG_NEEDS_TRACKING();
    const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);

    mg_str str = mg_str_new(mg_view_cstr("counted"));
    ASSERT_NE(str.items, nullptr);
    mg_str_free(&str);

    /* The characters and their terminator: "counted" is 7 and 1 more. */
    const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);
    EXPECT_EQ(after.allocated - before.allocated, 8u);
    EXPECT_EQ(after.deallocated - before.deallocated, 8u);
}

TEST(Copy, CopiesForward) {
    const u32 source[4] = {1, 2, 3, 4};
    u32 destination[4] = {0, 0, 0, 0};

    mg_mem_copy(destination, source, sizeof(source));

    for (int i = 0; i < 4; i++) {
        EXPECT_EQ(destination[i], source[i]);
    }
}

TEST(Copy, CopyingNothingTouchesNothing) {
    u32 destination[2] = {9, 9};
    mg_mem_copy(destination, destination, 0);
    EXPECT_EQ(destination[0], 9u);
    EXPECT_EQ(destination[1], 9u);
}

TEST(Copy, FillWritesEveryByte) {
    byte bytes[5] = {0, 0, 0, 0, 0};
    mg_mem_fill(bytes, sizeof(bytes), 0xAB);

    for (byte b : bytes) {
        EXPECT_EQ(b, 0xAB);
    }
}

TEST(Copy, ZeroIsFillWithZero) {
    byte bytes[5] = {1, 2, 3, 4, 5};
    mg_mem_zero(bytes, sizeof(bytes));

    for (byte b : bytes) {
        EXPECT_EQ(b, 0);
    }
}

TEST(Copy, MoveHandlesOverlapInBothDirections) {
    /* The case memcpy is undefined for and the reason every container shift
     * goes through move: source and destination are the same run of items, one
     * slot apart. Shifting right is the insert, shifting left is the remove. */
    u32 right[5] = {1, 2, 3, 4, 0};
    mg_mem_move(&right[1], &right[0], 4 * sizeof(u32));
    EXPECT_EQ(right[0], 1u);
    EXPECT_EQ(right[1], 1u);
    EXPECT_EQ(right[2], 2u);
    EXPECT_EQ(right[3], 3u);
    EXPECT_EQ(right[4], 4u);

    u32 left[5] = {1, 2, 3, 4, 5};
    mg_mem_move(&left[0], &left[1], 4 * sizeof(u32));
    EXPECT_EQ(left[0], 2u);
    EXPECT_EQ(left[1], 3u);
    EXPECT_EQ(left[2], 4u);
    EXPECT_EQ(left[3], 5u);
    EXPECT_EQ(left[4], 5u);
}

TEST(Copy, SwapExchangesBothSides) {
    u32 left[3] = {1, 2, 3};
    u32 right[3] = {7, 8, 9};

    mg_mem_swap(left, right, sizeof(left));

    EXPECT_EQ(left[0], 7u);
    EXPECT_EQ(left[2], 9u);
    EXPECT_EQ(right[0], 1u);
    EXPECT_EQ(right[2], 3u);
}

TEST(Copy, SwapWalksRegionsLargerThanItsScratchBuffer) {
    /* The scratch buffer is 256 bytes, so anything larger has to come back in
     * more than one pass -- and a pass boundary is where an off-by-one lands.
     * 1000 bytes is three full passes and a short one. */
    constexpr len size = 1000;
    byte left[size];
    byte right[size];

    for (len i = 0; i < size; i++) {
        left[i] = static_cast<byte>(i & 0xFF);
        right[i] = static_cast<byte>((i + 128) & 0xFF);
    }

    mg_mem_swap(left, right, size);

    for (len i = 0; i < size; i++) {
        EXPECT_EQ(left[i], static_cast<byte>((i + 128) & 0xFF)) << "at " << i;
        EXPECT_EQ(right[i], static_cast<byte>(i & 0xFF)) << "at " << i;
    }
}

TEST(Copy, SwappingARegionWithItselfLeavesItAlone) {
    u32 values[3] = {1, 2, 3};
    mg_mem_swap(values, values, sizeof(values));
    EXPECT_EQ(values[0], 1u);
    EXPECT_EQ(values[1], 2u);
    EXPECT_EQ(values[2], 3u);
}

} // namespace

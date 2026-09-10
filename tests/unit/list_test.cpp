/**
 * @file list_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/collections/list.h -- growth, and the typed spellings a mint
 * puts over it.
 *
 * A list IS a mg_buffer, so everything buffer_test.cpp covers holds for a
 * list too and is not repeated here -- mg_buffer_reserve's arithmetic among
 * it. What is tested is what list.h adds: the 8-slot floor its append puts in
 * front of reserve, the minted typed operations, and that a consumer can mint
 * a list of their own type.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

/* A struct curr, minted the way a caller would mint a list of their own type;
 * under the C++ umbrella too, which is what this file proves. */
struct point {
    i32 x;
    i32 y;
};
MG_DEFINE_NAMED_LIST(point, mg_point_list, NULL);

TEST(List, FirstAppendReservesEightSlots) {
    /* mg_buffer_reserve has no floor -- from a capacity of 0 it would grow
     * to 1. The 8 is list.h's append asking for MG_LIST_MIN_CAPACITY, so a
     * fresh list's first handful of appends is one allocation. */
    mg_u32_list xs = mg_u32_list_new(0);

    ASSERT_TRUE(mg_u32_list_push(&xs, 1));
    EXPECT_EQ(xs.capacity, MG_LIST_MIN_CAPACITY);

    /* And past the floor it doubles like any buffer. */
    for (u32 i = 2; i <= 9; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }
    EXPECT_EQ(xs.capacity, 16u);

    mg_u32_list_free(&xs);
}

TEST(List, StartsEmptyAndAllocatesOnFirstAppend) {
    mg_u32_list xs = mg_u32_list_new(0);

    EXPECT_EQ(xs.items, nullptr);
    EXPECT_EQ(xs.item_size, sizeof(u32));
    EXPECT_EQ(xs.count, 0u);
    EXPECT_EQ(xs.capacity, 0u);

    ASSERT_TRUE(mg_u32_list_push(&xs, 42));

    EXPECT_NE(xs.items, nullptr);
    EXPECT_EQ(xs.count, 1u);
    EXPECT_EQ(*mg_u32_list_at(&xs, 0), 42u);

    mg_u32_list_free(&xs);
}

TEST(List, TheMintedConstructIsTheEmptyList) {
    /* Same shape mg_u32_list_new(0) hands back: nothing allocated, the first
     * append does, and free works as usual. */
    mg_u32_list xs = mg_u32_list_construct();

    EXPECT_EQ(xs.items, nullptr);
    EXPECT_EQ(xs.item_size, sizeof(u32));
    EXPECT_EQ(xs.count, 0u);
    EXPECT_EQ(xs.capacity, 0u);

    ASSERT_TRUE(mg_u32_list_push(&xs, 7));
    EXPECT_EQ(*mg_u32_list_at(&xs, 0), 7u);

    mg_u32_list_free(&xs);
}

TEST(List, NewWithACapacityTakesExactlyThatMuch) {
    mg_u32_list xs = mg_u32_list_new(3);

    EXPECT_NE(xs.items, nullptr);
    EXPECT_EQ(xs.capacity, 3u);
    EXPECT_EQ(xs.count, 0u);

    mg_u32_list_free(&xs);
}

TEST(List, GrowsThroughManyAppendsAndKeepsEveryValue) {
    mg_u32_list xs = mg_u32_list_new(0);

    for (u32 i = 0; i < 100; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }

    EXPECT_EQ(xs.count, 100u);
    EXPECT_EQ(xs.capacity, 128u);
    for (u32 i = 0; i < 100; i++) {
        EXPECT_EQ(*mg_u32_list_at(&xs, i), i) << "slot " << i;
    }

    mg_u32_list_free(&xs);
}

TEST(List, TypedAtIsTheBuffersAtWithTheSizeChecked) {
    mg_u32_list xs = mg_u32_list_new(0);
    ASSERT_TRUE(mg_u32_list_push(&xs, 7));

    /* Same address, typed -- the cast lives in the mint, checked against the
     * list's curr size. */
    EXPECT_EQ(mg_u32_list_at(&xs, 0), mg_buffer_at(&xs, 0));

    *mg_u32_list_at(&xs, 0) = 9;
    EXPECT_EQ(*mg_u32_list_at(&xs, 0), 9u);

    mg_u32_list_free(&xs);
}

TEST(List, RemoveIsTheBuffersSwapRemove) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 1; i <= 4; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i)); // 1 2 3 4
    }

    mg_u32_list_remove(&xs, 0);

    /* The last curr fills the gap: O(1), order not preserved. */
    EXPECT_EQ(xs.count, 3u);
    EXPECT_EQ(*mg_u32_list_at(&xs, 0), 4u);
    EXPECT_EQ(*mg_u32_list_at(&xs, 1), 2u);
    EXPECT_EQ(*mg_u32_list_at(&xs, 2), 3u);

    mg_u32_list_free(&xs);
}

TEST(List, EmptyTracksTheCountThroughItsWholeLife) {
    mg_u32_list xs = mg_u32_list_new(0);

    /* Empty before it has ever allocated... */
    EXPECT_TRUE(mg_buffer_empty(&xs));

    ASSERT_TRUE(mg_u32_list_push(&xs, 1));
    EXPECT_FALSE(mg_buffer_empty(&xs));

    /* ...and empty again after a clear, which keeps the allocation. */
    mg_buffer_clear(&xs);
    EXPECT_TRUE(mg_buffer_empty(&xs));

    mg_u32_list_free(&xs);
    EXPECT_TRUE(mg_buffer_empty(&xs));
}

TEST(List, ClearKeepsTheAllocation) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 0; i < 5; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }
    void* items = xs.items;

    mg_buffer_clear(&xs);

    EXPECT_EQ(xs.count, 0u);
    EXPECT_EQ(xs.items, items);
    EXPECT_EQ(xs.capacity, 8u);

    mg_u32_list_free(&xs);
}

TEST(List, FreeLeavesAnEmptyListRatherThanAnInvalidOne) {
    mg_u32_list xs = mg_u32_list_new(0);
    ASSERT_TRUE(mg_u32_list_push(&xs, 1));

    mg_u32_list_free(&xs);
    EXPECT_EQ(xs.items, nullptr);
    EXPECT_EQ(xs.count, 0u);
    EXPECT_EQ(xs.capacity, 0u);

    /* Freeing twice is a no-op, and appending afterwards allocates again. */
    mg_u32_list_free(&xs);
    ASSERT_TRUE(mg_u32_list_push(&xs, 2));
    EXPECT_EQ(*mg_u32_list_at(&xs, 0), 2u);

    mg_u32_list_free(&xs);
}

TEST(List, IsABufferSoTheBufferMacrosWalkIt) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 1; i <= 4; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }

    u32 sum = 0;
    u32* item;
    mg_foreach(item, &xs) {
        sum += *item;
    }
    EXPECT_EQ(sum, 10u);

    u32 evens = 0;
    mg_where(item, &xs, *item % 2 == 0) {
        evens += *item;
    }
    EXPECT_EQ(evens, 6u);

    EXPECT_EQ(*static_cast<u32*>(mg_first(&xs)), 1u);
    EXPECT_EQ(*static_cast<u32*>(mg_last(&xs)), 4u);

    mg_u32_list_free(&xs);
}

TEST(List, TheViewSpansTheLiveItems) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 1; i <= 3; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }

    const mg_view view = mg_u32_list_view(&xs);
    EXPECT_EQ(view.items, xs.items);
    EXPECT_EQ(view.item_size, sizeof(u32));
    EXPECT_EQ(view.count, 3u);

    mg_u32_list_free(&xs);
}

TEST(List, TheTypedIteratorWalksBothWays) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 1; i <= 3; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }

    mg_u32_list_iter it = mg_u32_list_it(&xs);
    u32 expected = 1;
    const u32* x;
    while ((x = mg_u32_list_iter_next(&it)) != nullptr) {
        EXPECT_EQ(*x, expected);
        expected++;
    }
    EXPECT_EQ(expected, 4u);
    EXPECT_EQ(mg_u32_list_iter_next(&it), nullptr);

    mg_u32_list_iter rit = mg_u32_list_rit(&xs);
    expected = 3;
    while ((x = mg_u32_list_iter_previous(&rit)) != nullptr) {
        EXPECT_EQ(*x, expected);
        expected--;
    }
    EXPECT_EQ(expected, 0u);

    mg_u32_list_free(&xs);
}

TEST(List, PushManyGrowsAndPopPeekShrink) {
    mg_u32_list xs = mg_u32_list_new(0);

    /* From nothing: push_many reserves with the same 8-slot floor push uses,
     * then lands the whole run at once. */
    const u32 run[3] = {1, 2, 3};
    ASSERT_TRUE(mg_u32_list_push_many(&xs, run, 3));
    EXPECT_EQ(xs.count, 3u);
    EXPECT_EQ(xs.capacity, MG_LIST_MIN_CAPACITY);

    EXPECT_EQ(*mg_u32_list_peek(&xs), 3u);
    EXPECT_EQ(mg_u32_list_pop(&xs), 3u);
    EXPECT_EQ(*mg_u32_list_peek(&xs), 2u);
    EXPECT_EQ(xs.count, 2u);

    /* Zero items is a successful nothing, even from NULL. */
    ASSERT_TRUE(mg_u32_list_push_many(&xs, nullptr, 0));
    EXPECT_EQ(xs.count, 2u);

    mg_u32_list_free(&xs);
}

TEST(List, PopFrontDrainsOldestFirst) {
    mg_u32_list xs = mg_u32_list_new(0);
    for (u32 i = 1; i <= 3; i++) {
        ASSERT_TRUE(mg_u32_list_push(&xs, i));
    }

    /* The queue shape: push feeds the back, pop_front drains the front, so
     * items come out in the order they went in. */
    EXPECT_EQ(mg_u32_list_pop_front(&xs), 1u);
    EXPECT_EQ(mg_u32_list_pop_front(&xs), 2u);
    EXPECT_EQ(mg_u32_list_pop_front(&xs), 3u);

    /* The empty pop_front hands back the zeroed curr, like pop. */
    EXPECT_EQ(xs.count, 0u);
    EXPECT_EQ(mg_u32_list_pop_front(&xs), 0u);

    mg_u32_list_free(&xs);
}

TEST(List, AConsumerMintedStructListCarriesWholeValues) {
    mg_point_list ps = mg_point_list_new(0);

    ASSERT_TRUE(mg_point_list_push(&ps, {1, 2}));
    ASSERT_TRUE(mg_point_list_push(&ps, {3, 4}));

    EXPECT_EQ(ps.item_size, sizeof(point));
    EXPECT_EQ(mg_point_list_at(&ps, 0)->x, 1);
    EXPECT_EQ(mg_point_list_at(&ps, 1)->y, 4);

    mg_point_list_free(&ps);
}

} // namespace

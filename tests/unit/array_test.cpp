/**
 * @file array_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/collections/array.h -- the fixed-capacity array, minted per type.
 *
 * An array is a mg_buffer that owns one allocation and never grows, so
 * everything buffer_test.cpp covers holds here too. What is tested is the
 * ownership story -- new allocates, free releases, freeing twice is a no-op --
 * and the typed spellings a mint puts over the buffer: append by value, at as
 * a type*, the view, and the typed iterator.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

    /**
     * @brief Counts what the mint destroys, so the baked-in destructor can
     * be asserted on.
     */
    int g_owned_destroyed = 0;

    extern "C" void count_owned_destruction(void* item) {
        (void)item;
        g_owned_destroyed++;
    }

    /* A mint with a destructor, the way a caller whose items own something
     * would mint one: stored once here, never passed at a call site. */
    MG_DEFINE_NAMED_ARRAY(u32, mg_owned_array, count_owned_destruction);

    TEST(Array, TheArrayIsTheStorage) {
        /* The whole construction story: ask for the capacity, get the slots.
     * Nothing to declare beside it, and free is the only cleanup. */
        mg_u32_array scores = mg_u32_array_new(16);

        EXPECT_NE(scores.items, nullptr);
        EXPECT_EQ(scores.item_size, sizeof(u32));
        EXPECT_EQ(scores.count, 0u);
        EXPECT_EQ(scores.capacity, 16u);

        mg_u32_array_free(&scores);
        EXPECT_EQ(scores.items, nullptr);
        EXPECT_EQ(scores.count, 0u);
        EXPECT_EQ(scores.capacity, 0u);

        /* Freeing twice is a no-op by contract. */
        mg_u32_array_free(&scores);
        EXPECT_EQ(scores.items, nullptr);
    }

    TEST(Array, ZeroCapacityIsAlreadyFull) {
        /* The out-of-memory shape and the new(0) shape are the same array:
     * no slots, so full and empty at once, and free is a no-op. */
        mg_u32_array none = mg_u32_array_new(0);

        EXPECT_EQ(none.items, nullptr);
        EXPECT_TRUE(mg_buffer_empty(&none));
        EXPECT_TRUE(mg_buffer_full(&none));

        mg_u32_array_free(&none);
    }

    TEST(Array, FullAndEmptyBracketTheAppendLoop) {
        /* Appending to a full array asserts -- it is a caller bug, not an outcome
     * to test for -- so the honest fill loop is gated on full, and that gate
     * is what this test exercises. */
        mg_u32_array scores = mg_u32_array_new(2);

        EXPECT_TRUE(mg_buffer_empty(&scores));
        EXPECT_FALSE(mg_buffer_full(&scores));

        u32 value = 1;
        while (!mg_buffer_full(&scores)) {
            mg_u32_array_push(&scores, value);
            value++;
        }

        EXPECT_EQ(scores.count, 2u);
        EXPECT_TRUE(mg_buffer_full(&scores));
        EXPECT_FALSE(mg_buffer_empty(&scores));
        EXPECT_EQ(*mg_u32_array_at(&scores, 0), 1u);
        EXPECT_EQ(*mg_u32_array_at(&scores, 1), 2u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, TypedAtIsTheBuffersAtWithTheCastInside) {
        mg_u32_array scores = mg_u32_array_new(8);

        for (u32 i = 1; i <= 4; i++) {
            mg_u32_array_push(&scores, i); // 1 2 3 4
        }

        /* Same address as the untyped at; the mint only adds the type. */
        EXPECT_EQ(mg_u32_array_at(&scores, 1), mg_buffer_at(&scores, 1));
        *mg_u32_array_at(&scores, 1) = 20;
        EXPECT_EQ(*mg_u32_array_at(&scores, 1), 20u);

        /* remove is the buffer's remove: the last curr fills the gap, O(1),
     * order not preserved. */
        mg_u32_array_remove(&scores, 0);
        EXPECT_EQ(scores.count, 3u);
        EXPECT_EQ(*mg_u32_array_at(&scores, 0), 4u);

        /* clear keeps the allocation; only free gives it back. */
        mg_buffer_clear(&scores);
        EXPECT_EQ(scores.count, 0u);
        EXPECT_EQ(scores.capacity, 8u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, ItemAddressesAreStableForTheArraysWholeLife) {
        /* One allocation, never regrown: the address of an curr is the address
     * of a slot, for as long as the array lives. */
        mg_u32_array scores = mg_u32_array_new(8);

        mg_u32_array_push(&scores, 7);
        const u32* head = mg_u32_array_at(&scores, 0);

        for (u32 i = 0; i < 7; i++) {
            mg_u32_array_push(&scores, i);
        }

        EXPECT_EQ(mg_u32_array_at(&scores, 0), head);
        EXPECT_EQ(*head, 7u);

        mg_u32_array_free(&scores);
    }

    /* mg_array_construct has no C++ test: its storage is a compound literal,
     * which C keeps alive to the end of the enclosing block but C++ destroys
     * at the end of the declaration -- in this suite the array would dangle.
     * The macro is for C translation units; C++ callers pass their own
     * storage to the minted construct, tested next. */

    TEST(Array, TheMintedConstructWrapsCallerStorage) {
        /* The minted construct cannot conjure storage -- a function's compound
     * literal dies with the call -- so the caller declares it and the array
     * borrows it: same slots, never freed. */
        u32 slots[4] = {0};
        mg_u32_array locals = mg_u32_array_construct(slots, 4);

        EXPECT_EQ(locals.items, slots);
        EXPECT_EQ(locals.item_size, sizeof(u32));
        EXPECT_EQ(locals.count, 0u);
        EXPECT_EQ(locals.capacity, 4u);

        mg_u32_array_push(&locals, 9);
        EXPECT_EQ(slots[0], 9u);

        /* NULL storage is an array that is already full, not a crash. */
        mg_u32_array none = mg_u32_array_construct(nullptr, 4);
        EXPECT_EQ(none.capacity, 0u);
        EXPECT_TRUE(mg_buffer_full(&none));
    }

    TEST(Array, IndexOfFindsByValueOrSaysNotFound) {
        mg_u32_array scores = mg_u32_array_new(4);
        for (u32 i = 1; i <= 3; i++) {
            mg_u32_array_push(&scores, i * 10); // 10 20 30
        }

        /* A value search, not an address one: the 20 searched for lives on
     * this stack frame, and the match is byte equality. */
        EXPECT_EQ(mg_u32_array_index_of(&scores, 20), 1u);
        EXPECT_EQ(mg_u32_array_index_of(&scores, 99), MG_NOT_FOUND);

        /* Remove is the swap remove, so what fills index 0 is the last. */
        mg_u32_array_remove(&scores, 0);
        EXPECT_EQ(mg_u32_array_index_of(&scores, 30), 0u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, BufferMacrosWalkIt) {
        mg_u32_array scores = mg_u32_array_new(8);

        for (u32 i = 1; i <= 3; i++) {
            mg_u32_array_push(&scores, i);
        }

        u32 sum = 0;
        u32* score;
        mg_foreach(score, &scores) {
            sum += *score;
        }
        EXPECT_EQ(sum, 6u);

        EXPECT_EQ(*static_cast<u32*>(mg_first(&scores)), 1u);
        EXPECT_EQ(*static_cast<u32*>(mg_last(&scores)), 3u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, TheViewSpansTheLiveItems) {
        mg_u32_array scores = mg_u32_array_new(8);
        for (u32 i = 1; i <= 3; i++) {
            mg_u32_array_push(&scores, i);
        }

        const mg_view view = mg_u32_array_view(&scores);
        EXPECT_EQ(view.items, scores.items);
        EXPECT_EQ(view.item_size, sizeof(u32));
        EXPECT_EQ(view.count, 3u);

        /* The typed view feeds the generic iterator like any view does. */
        mg_iter it = mg_iter_construct(view);
        EXPECT_EQ(*static_cast<const u32*>(mg_iter_next(&it)), 1u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, TheTypedIteratorWalksForward) {
        mg_u32_array scores = mg_u32_array_new(4);
        for (u32 i = 1; i <= 3; i++) {
            mg_u32_array_push(&scores, i);
        }

        mg_u32_array_iter it = mg_u32_array_it(&scores);
        u32 expected = 1;
        const u32* score;
        while ((score = mg_u32_array_iter_next(&it)) != nullptr) {
            EXPECT_EQ(*score, expected);
            expected++;
        }
        EXPECT_EQ(expected, 4u);

        /* Exhaustion is sticky: the walk ended, and says so again. */
        EXPECT_EQ(mg_u32_array_iter_next(&it), nullptr);

        mg_u32_array_free(&scores);
    }

    TEST(Array, PushManyPopAndPeekSpeakTheStackShape) {
        mg_u32_array scores = mg_u32_array_new(8);

        const u32 run[3] = {1, 2, 3};
        mg_u32_array_push_many(&scores, run, 3);
        EXPECT_EQ(scores.count, 3u);

        /* Pop hands the curr back by value, ownership included; the empty
     * pop hands back the zeroed curr. */
        EXPECT_EQ(*mg_u32_array_peek(&scores), 3u);
        EXPECT_EQ(mg_u32_array_pop(&scores), 3u);
        EXPECT_EQ(*mg_u32_array_peek(&scores), 2u);
        EXPECT_EQ(scores.count, 2u);

        EXPECT_EQ(mg_u32_array_pop(&scores), 2u);
        EXPECT_EQ(mg_u32_array_pop(&scores), 1u);
        EXPECT_EQ(mg_u32_array_peek(&scores), nullptr);
        EXPECT_EQ(mg_u32_array_pop(&scores), 0u);

        mg_u32_array_free(&scores);
    }

    TEST(Array, TheTypedReverseIteratorWalksBackward) {
        mg_u32_array scores = mg_u32_array_new(4);
        for (u32 i = 1; i <= 3; i++) {
            mg_u32_array_push(&scores, i);
        }

        mg_u32_array_iter it = mg_u32_array_rit(&scores);
        u32 expected = 3;
        const u32* score;
        while ((score = mg_u32_array_iter_previous(&it)) != nullptr) {
            EXPECT_EQ(*score, expected);
            expected--;
        }
        EXPECT_EQ(expected, 0u);
        EXPECT_EQ(mg_u32_array_iter_previous(&it), nullptr);

        mg_u32_array_free(&scores);
    }

    TEST(Array, TheMintedDestructorRunsWithoutBeingPassed) {
        g_owned_destroyed = 0;

        mg_owned_array owned = mg_owned_array_new(4);
        for (u32 i = 1; i <= 3; i++) {
            mg_owned_array_push(&owned, i);
        }
        EXPECT_EQ(owned.destructor, count_owned_destruction);

        /* Remove destroys the one curr it takes out... */
        mg_owned_array_remove(&owned, 0);
        EXPECT_EQ(g_owned_destroyed, 1);

        /* ...and free destroys the live remainder before the storage goes.
     * No call site named the destructor; the mint did, once. */
        mg_owned_array_free(&owned);
        EXPECT_EQ(g_owned_destroyed, 3);
    }

} // namespace

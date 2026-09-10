/**
 * @file string_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>
#include <magic.h>

namespace {

    /** @brief Asserts an owned string holds expected and is terminated. */
    void expect_str(const mg_str str, const char* expected) {
        ASSERT_NE(str.items, nullptr) << "the allocation failed, or was never made";
        EXPECT_STREQ(mg_str_to_cstr(str), expected);
        EXPECT_EQ(str.count, mg_cstr_length(expected));
        EXPECT_EQ(str.items[str.count], '\0') << "no terminator past the last character";
    }

    /**
 * @brief Asserts a view spans exactly expected's characters. No terminator
 * check: a view makes no promise of one.
 */
    void expect_view(const mg_view view, const char* expected) {
        EXPECT_EQ(view.count, mg_cstr_length(expected));
        EXPECT_TRUE(mg_str_eq(view, mg_view_cstr(expected)));
    }

    TEST(CstrLength, MeasuresToTheTerminator) {
        EXPECT_EQ(mg_cstr_length("hello"), 5u);
        EXPECT_EQ(mg_cstr_length(""), 0u);
    }

    TEST(CstrLength, NullIsZeroRatherThanACrash) {
        EXPECT_EQ(mg_cstr_length(nullptr), 0u);
    }

    TEST(View, ViewOfALiteralSpansItsCharacters) {
        const mg_view v = mg_view_cstr("hello");

        EXPECT_EQ(v.count, 5u);
        expect_view(v, "hello");

        /* The empty literal is a valid, zero-length view. */
        EXPECT_EQ(mg_view_cstr("").count, 0u);
    }

    TEST(View, ViewCstrWalksTheCharactersOnce) {
        expect_view(mg_view_cstr("hello"), "hello");

        /* NULL views as empty, so "no string" and "empty string" flow the same. */
        const mg_view none = mg_view_cstr(nullptr);
        EXPECT_EQ(none.count, 0u);
    }

    TEST(View, ViewOfAStringSpansAllOfIt) {
        mg_str s = mg_str_new(mg_view_cstr("hello"));

        const mg_view v = mg_view_str(s);
        EXPECT_EQ(v.items, s.items) << "a borrow, not a copy";
        EXPECT_EQ(v.count, s.count);

        mg_str_free(&s);
    }

    TEST(View, EmptyIsNoItemsOrNoneOfThem) {
        EXPECT_FALSE(mg_str_view_empty(mg_view_cstr("hello")));

        /* Both shapes of empty: no characters at all, and characters spanning
         * none of them. */
        EXPECT_TRUE(mg_str_view_empty(mg_view_cstr(nullptr)));
        EXPECT_TRUE(mg_str_view_empty(mg_view_cstr("")));

        /* The out-of-memory string views as empty, which is what lets a failed
         * allocation flow on as "nothing here". */
        const mg_str none = {nullptr, 0};
        EXPECT_TRUE(mg_str_view_empty(mg_view_str(none)));
    }

    TEST(Str, NewStrCopiesTheViewedCharacters) {
        const char* source = "hello";
        mg_str s = mg_str_new(mg_view_cstr(source));

        expect_str(s, "hello");
        /* A copy, not a borrow: the string's characters are its own allocation. */
        EXPECT_NE(static_cast<const void*>(s.items), static_cast<const void*>(source));

        mg_str_free(&s);
    }

    TEST(Str, NewStrFromAnEmptyViewIsAnEmptyStringAndNotAFailedOne) {
        mg_str s = mg_str_new(mg_view_cstr(nullptr));

        /* The terminator still allocated, so characters is non-NULL -- which is
     * what keeps `characters == NULL` meaning out of memory and nothing else. */
        expect_str(s, "");

        mg_str_free(&s);
    }

    TEST(Str, NewStrFromAnotherStringIsACopy) {
        mg_str original = mg_str_new(mg_view_cstr("hello"));
        mg_str copy = mg_str_new(mg_view_str(original));

        expect_str(copy, "hello");
        EXPECT_NE(copy.items, original.items);
        EXPECT_TRUE(mg_str_eq(mg_view_str(copy), mg_view_str(original)));

        /* Each owns its characters, so each is freed on its own. */
        mg_str_free(&original);
        expect_str(copy, "hello");

        mg_str_free(&copy);
    }

    TEST(Str, TheZeroedStructIsTheOutOfMemoryString) {
        mg_str s = {nullptr, 0};

        EXPECT_EQ(mg_str_to_cstr(s), nullptr);
        /* And freeing it is a no-op, not a crash. */
        mg_str_free(&s);
        SUCCEED();
    }

    TEST(Str, AppendBuildsANewStringAndTouchesNeitherInput) {
        mg_str a = mg_str_new(mg_view_cstr("hello"));
        mg_str b = mg_str_new(mg_view_cstr(", world"));

        mg_str joined = mg_str_append(mg_view_str(a), mg_view_str(b));

        expect_str(joined, "hello, world");
        /* Immutable means immutable: both inputs are exactly as they were. */
        expect_str(a, "hello");
        expect_str(b, ", world");

        mg_str_free(&a);
        mg_str_free(&b);
        mg_str_free(&joined);
    }

    TEST(Str, AppendingALiteralViewSpellsTheCommonCase) {
        mg_str s = mg_str_new(mg_view_cstr("hello"));

        /* No string made just to concatenate: the literal rides in as a view. */
        mg_str joined = mg_str_append(mg_view_str(s), mg_view_cstr(", world"));

        expect_str(joined, "hello, world");
        expect_str(s, "hello");

        mg_str_free(&s);
        mg_str_free(&joined);
    }

    TEST(Str, AppendWithItselfIsJustTwoOfIt) {
        /* No aliasing trap anywhere: the result is a third allocation, so the two
     * halves are read from an input nothing is writing to. */
        mg_str s = mg_str_new(mg_view_cstr("ab"));

        mg_str doubled = mg_str_append(mg_view_str(s), mg_view_str(s));

        expect_str(doubled, "abab");
        expect_str(s, "ab");

        mg_str_free(&s);
        mg_str_free(&doubled);
    }

    TEST(Str, AppendWithEmptyAndWithNothing) {
        mg_str s = mg_str_new(mg_view_cstr("hello"));
        const mg_view nothing = {nullptr, 0, 0};

        mg_str a = mg_str_append(mg_view_str(s), mg_view_cstr(""));
        expect_str(a, "hello");

        /* The out-of-memory string's view as an input contributes zero characters
     * rather than needing a check at the call site. */
        mg_str b = mg_str_append(mg_view_str(s), nothing);
        expect_str(b, "hello");

        mg_str c = mg_str_append(mg_view_str(s), mg_view_cstr(nullptr));
        expect_str(c, "hello");

        mg_str_free(&s);
        mg_str_free(&a);
        mg_str_free(&b);
        mg_str_free(&c);
    }

    TEST(View, TrimIsANarrowerViewOfTheSameCharacters) {
        mg_str s = mg_str_new(mg_view_cstr("  hello   world  "));

        const mg_view trimmed = mg_str_trim(mg_view_str(s));

        expect_view(trimmed, "hello   world");
        /* A narrower borrow of s's own characters: nothing allocated, nothing to
     * free, and the whitespace is still on the original. */
        EXPECT_EQ(trimmed.items, s.items + 2);
        expect_str(s, "  hello   world  ");

        mg_str_free(&s);
    }

    TEST(View, TrimLeftAndRightEachTakeOneEnd) {
        const mg_view v = mg_view_cstr(" \t\r\nhello \t\r\n");

        expect_view(mg_str_trim_left(v), "hello \t\r\n");
        expect_view(mg_str_trim_right(v), " \t\r\nhello");
        expect_view(v, " \t\r\nhello \t\r\n");
    }

    TEST(View, TrimmingWhitespaceOnlyLeavesAValidEmptyView) {
        const mg_view trimmed = mg_str_trim(mg_view_cstr(" \t\n\v\f\r "));
        EXPECT_EQ(trimmed.count, 0u);
    }

    TEST(View, TrimAllocatesNothing) {
        /* A trim only narrows the borrow, so the string tag's books must not move
     * at all. */
        const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);
        const mg_view trimmed = mg_str_trim(mg_view_cstr("  hello  "));
        const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);

        expect_view(trimmed, "hello");
        EXPECT_EQ(after.allocated, before.allocated);
        EXPECT_EQ(after.deallocated, before.deallocated);
    }

    TEST(Str, OwningATrimmedViewIsOneNewStr) {
        /* The composition the split is for: view operations narrow for free, and
     * one mg_str_new at the end makes the result owned and terminated. */
        mg_str s = mg_str_new(mg_str_trim(mg_view_cstr("  hello  ")));

        expect_str(s, "hello");

        mg_str_free(&s);
    }

    TEST(View, EqComparesCharacters) {
        EXPECT_TRUE(mg_str_eq(mg_view_cstr("hello"), mg_view_cstr("hello")));
        EXPECT_FALSE(mg_str_eq(mg_view_cstr("hello"), mg_view_cstr("hellp")));
        EXPECT_FALSE(mg_str_eq(mg_view_cstr("hello"), mg_view_cstr("hell")));
    }

    TEST(View, ContainsFindsTheSubstringAtEveryPosition) {
        const mg_view s = mg_view_cstr("hello, world");

        EXPECT_TRUE(mg_str_contains(s, mg_view_cstr("hello")));
        EXPECT_TRUE(mg_str_contains(s, mg_view_cstr("o, w")));
        EXPECT_TRUE(mg_str_contains(s, mg_view_cstr("world")));
        EXPECT_FALSE(mg_str_contains(s, mg_view_cstr("word")));
        /* Longer than the haystack: can't fit, and must not read past the end. */
        EXPECT_FALSE(mg_str_contains(s, mg_view_cstr("hello, world!")));
    }

    TEST(View, ContainsTheEmptyStringInEverything) {
        const mg_view nothing = {nullptr, 0, 0};

        EXPECT_TRUE(mg_str_contains(mg_view_cstr("hello"), mg_view_cstr("")));
        EXPECT_TRUE(mg_str_contains(mg_view_cstr("hello"), nothing));
        EXPECT_TRUE(mg_str_contains(nothing, nothing));
    }

    TEST(View, ContainsBorrowsRatherThanAllocates) {
        /* A search only looks at the characters, so the string tag's books must
     * not move at all -- an allocation per candidate window would leak here. */
        const mg_memory_stats before = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);
        EXPECT_TRUE(mg_str_contains(
            mg_view_cstr("the quick brown fox"),
            mg_view_cstr("fox")));
        const mg_memory_stats after = mg_get_mem_usage_for(MG_MEMORY_TAG_STRING);

        EXPECT_EQ(after.allocated, before.allocated);
        EXPECT_EQ(after.deallocated, before.deallocated);
    }

    TEST(View, TheGenericIteratorWalksAStringsCharacters) {
        /* The point of one view shape: the buffer's iterator reads a string with
     * no string-specific spelling. */
        mg_iter it = mg_iter_construct(mg_view_cstr("abc"));

        std::string walked;
        while (mg_iter_next(&it)) {
            walked += *static_cast<const char*>(it.curr);
        }
        EXPECT_EQ(walked, "abc");

        mg_iter rit = mg_iter_construct_reverse(mg_view_cstr("abc"));
        std::string backwards;
        while (mg_iter_previous(&rit)) {
            backwards += *static_cast<const char*>(rit.curr);
        }
        EXPECT_EQ(backwards, "cba");
    }

    TEST(Str, EmptyStringsAreEqualHoweverTheyWereMade) {
        mg_str allocated = mg_str_new(mg_view_cstr(""));
        const mg_view nothing = {nullptr, 0, 0};

        /* Same characters -- none -- so equal, even though one allocated its
     * terminator and the other is the out-of-memory string's view. */
        EXPECT_TRUE(mg_str_eq(mg_view_str(allocated), nothing));

        mg_str_free(&allocated);
    }

    TEST(Str, FreeLeavesTheEmptyStringAndIsRepeatable) {
        mg_str s = mg_str_new(mg_view_cstr("hello"));

        mg_str_free(&s);
        EXPECT_EQ(s.items, nullptr);
        EXPECT_EQ(s.count, 0u);

        mg_str_free(&s);
        SUCCEED();
    }

    TEST(StrList, CarriesOwnedStringsTheCallerFrees) {
        /* The minted list of strings: the list owns its slots, each string still
     * owns its characters, so teardown is one mg_str_free per string and then
     * the list's own free. */
        mg_str_list names = mg_str_list_new(0);

        ASSERT_TRUE(mg_str_list_push(&names, mg_str_new(mg_view_cstr("alpha"))));
        ASSERT_TRUE(mg_str_list_push(&names, mg_str_new(mg_view_cstr("beta"))));
        EXPECT_EQ(names.count, 2u);
        EXPECT_EQ(names.item_size, sizeof(mg_str));

        expect_str(*mg_str_list_at(&names, 0), "alpha");
        expect_str(*mg_str_list_at(&names, 1), "beta");

        /* The typed iterator hands back const mg_str*, one per step. */
        mg_str_list_iter it = mg_str_list_it(&names);
        len walked = 0;
        const mg_str* name;
        while ((name = mg_str_list_iter_next(&it)) != nullptr) {
            EXPECT_NE(name->items, nullptr);
            walked++;
        }
        EXPECT_EQ(walked, 2u);

        mg_str* owned;
        mg_foreach(owned, &names) {
            mg_str_free(owned);
        }
        mg_str_list_free(&names);
    }

} // namespace

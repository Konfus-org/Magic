/**
 * @file files_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/files/parsing.h.
 *
 * Only the parsing half of the files subsystem: file.h and dirs.h forward to
 * platform slots, which have no gem loaded under a unit test.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

TEST(Parsing, ReadsEachTypeAtItsWidth) {
    EXPECT_EQ(mg_parse_u8("255", 0), 255);
    EXPECT_EQ(mg_parse_u16("65535", 0), 65535);
    EXPECT_EQ(mg_parse_u32("4294967295", 0), U32_MAX);
    EXPECT_EQ(mg_parse_u64("18446744073709551615", 0), U64_MAX);
    EXPECT_EQ(mg_parse_i8("-128", 0), I8_MIN);
    EXPECT_EQ(mg_parse_i16("-32768", 0), I16_MIN);
    EXPECT_EQ(mg_parse_i32("2147483647", 0), I32_MAX);
    EXPECT_EQ(mg_parse_i64("-9223372036854775808", 0), I64_MIN);
    EXPECT_FLOAT_EQ(mg_parse_f32("1.5", 0.0F), 1.5F);
    EXPECT_DOUBLE_EQ(mg_parse_f64("-2.5e3", 0.0), -2500.0);
}

TEST(Parsing, MalformedTextIsTheFallback) {
    EXPECT_EQ(mg_parse_u32(nullptr, 7), 7U);
    EXPECT_EQ(mg_parse_u32("", 7), 7U);
    EXPECT_EQ(mg_parse_u32("abc", 7), 7U);
    EXPECT_EQ(mg_parse_i32("--5", 7), 7);
    EXPECT_DOUBLE_EQ(mg_parse_f64("one", 7.0), 7.0);
}

TEST(Parsing, TrailingTextIsMalformedNotAPrefix) {
    EXPECT_EQ(mg_parse_u32("12abc", 7), 7U);
    EXPECT_EQ(mg_parse_i32("12 ", 7), 7);
    EXPECT_DOUBLE_EQ(mg_parse_f64("1.5x", 7.0), 7.0);
}

TEST(Parsing, LeadingWhitespaceIsFine) {
    EXPECT_EQ(mg_parse_u32("  12", 7), 12U);
    EXPECT_EQ(mg_parse_i32("\t-12", 7), -12);
}

TEST(Parsing, OutOfRangeIsTheFallbackNotAWrapAround) {
    EXPECT_EQ(mg_parse_u8("256", 7), 7);
    EXPECT_EQ(mg_parse_u16("65536", 7), 7);
    EXPECT_EQ(mg_parse_u32("4294967296", 7), 7U);
    EXPECT_EQ(mg_parse_u64("18446744073709551616", 7), 7U);
    EXPECT_EQ(mg_parse_i8("128", 7), 7);
    EXPECT_EQ(mg_parse_i16("-32769", 7), 7);
    EXPECT_EQ(mg_parse_i32("2147483648", 7), 7);
    EXPECT_EQ(mg_parse_i64("9223372036854775808", 7), 7);
}

TEST(Parsing, NegativesNeverWrapIntoUnsigned) {
    /* strtoull itself would read -1 as U64_MAX; the parse refuses instead. */
    EXPECT_EQ(mg_parse_u32("-1", 7), 7U);
    EXPECT_EQ(mg_parse_u64("-1", 7), 7U);
}

} // namespace

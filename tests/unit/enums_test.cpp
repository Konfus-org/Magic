/**
 * @file enums_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/enums.h -- the printable-enum mint. What is tested is the
 * promise mg_get_enum_name makes: every value inside a minted enum's count
 * names as its own spelling, and anything past the count -- a custom code,
 * a stray integer -- comes back as "?" rather than a read off the table.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

    TEST(Enums, FirstValueNamesAsItsSpelling) {
        EXPECT_STREQ("MG_EVENT_CODE_NONE", mg_get_enum_name(mg_event_code, MG_EVENT_CODE_NONE));
    }

    TEST(Enums, MidTableValuesNameAcrossFamilies) {
        EXPECT_STREQ(
            "MG_MEMORY_TAG_PLATFORM",
            mg_get_enum_name(mg_memory_tag, MG_MEMORY_TAG_PLATFORM));
        EXPECT_STREQ("MG_KEY_W", mg_get_enum_name(mg_key_code, MG_KEY_W));
        EXPECT_STREQ(
            "MG_LOG_LEVEL_WARN",
            mg_get_enum_name(mg_log_level, MG_LOG_LEVEL_WARN));
    }

    TEST(Enums, LastValueBeforeCountNames) {
        EXPECT_STREQ(
            "MG_EVENT_CODE_COMMAND_SENT",
            mg_get_enum_name(mg_event_code, MG_EVENT_CODE_COUNT - 1));
        EXPECT_STREQ(
            "MG_KEY_NUMPAD_PERIOD",
            mg_get_enum_name(mg_key_code, MG_KEY_COUNT - 1));
    }

    TEST(Enums, ValuesPastTheCountNameAsUnknown) {
        EXPECT_STREQ("?", mg_get_enum_name(mg_event_code, MG_EVENT_CODE_COUNT));
        EXPECT_STREQ("?", mg_get_enum_name(mg_event_code, MG_EVENT_CODE_COUNT + 1));
        EXPECT_STREQ("?", mg_get_enum_name(mg_memory_tag, U32_MAX));
    }

} // namespace

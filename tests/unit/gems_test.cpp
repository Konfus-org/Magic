/**
 * @file gems_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/gems/slots.h -- the slot registry: what registration captures,
 * what a repeat or a collision does, that the registry grows without a cap,
 * and that the platform family's slots land in it holding their defaults.
 *
 * The registry is process-global and never shrinks, so every expectation here
 * is a delta or a contains, never an exact total; tests must hold in any
 * order and alongside whatever another suite registered.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

#include <cstdio>
#include <cstring>

namespace {

/** @brief How often the fake default ran; RestoreFromDefaultFn watches it. */
int g_fake_calls = 0;

void fake_default() {
    g_fake_calls++;
}

void fake_replacement() {}

/* A slot the way a family's slots.c makes one: a pointer starting at its
 * default. Registered by mg_register_gem_slot directly rather than
 * MG_REGISTER_GEM_SLOT, so the curr is a test-owned string and cannot collide
 * with a real slot's. */
void (*fake_slot)() = fake_default;

const mg_gem_slot* find_slot(const char* name) {
    const mg_gem_slot_table table = mg_gem_slots();
    for (len i = 0; i < table.count; i++) {
        if (std::strcmp(table.items[i].name, name) == 0) {
            return &table.items[i];
        }
    }
    return nullptr;
}

TEST(GemSlotRegistry, CapturesCurrentValueAsDefault) {
    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&fake_slot);

    const mg_gem_slot* slot = find_slot("test_fake_slot");
    ASSERT_NE(slot, nullptr);
    EXPECT_EQ(slot->ptr_address, (mg_lib_fn*)&fake_slot);
    EXPECT_EQ(slot->default_fn, (mg_lib_fn)fake_default);
}

TEST(GemSlotRegistry, ReregisteringSamePointerIsANoOp) {
    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&fake_slot);
    const len before = mg_gem_slots().count;

    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&fake_slot);
    EXPECT_EQ(mg_gem_slots().count, before);
}

TEST(GemSlotRegistry, NameCollisionIsDropped) {
    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&fake_slot);
    const len before = mg_gem_slots().count;

    static void (*imposter)() = fake_default;
    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&imposter);

    EXPECT_EQ(mg_gem_slots().count, before);
    const mg_gem_slot* slot = find_slot("test_fake_slot");
    ASSERT_NE(slot, nullptr);
    EXPECT_EQ(slot->ptr_address, (mg_lib_fn*)&fake_slot);
}

TEST(GemSlotRegistry, GrowsWithoutACap) {
    /* Static storage, because the registry borrows both the pointer and the
     * curr for the life of the process. 32 is comfortably past the list's
     * first allocation, so at least one growth realloc happens mid-loop. */
    constexpr int grown = 32;
    static void (*grow_slots[grown])() = {};
    static char grow_names[grown][32] = {};

    const len before = mg_gem_slots().count;
    for (int i = 0; i < grown; i++) {
        grow_slots[i] = fake_default;
        std::snprintf(grow_names[i], sizeof(grow_names[i]), "test_grow_slot_%d", i);
        mg_register_gem_slot(grow_names[i], (mg_lib_fn*)&grow_slots[i]);
    }

    EXPECT_EQ(mg_gem_slots().count, before + grown);
    for (int i = 0; i < grown; i++) {
        const mg_gem_slot* slot = find_slot(grow_names[i]);
        ASSERT_NE(slot, nullptr);
        EXPECT_EQ(slot->ptr_address, (mg_lib_fn*)&grow_slots[i]);
    }
}

TEST(GemSlotRegistry, PlatformFamilyRegistersEverySlotAtItsDefault) {
    mg_platform_register_gem_slots();
    const len after_first = mg_gem_slots().count;

    // The second call proves the family function is safe to call again.
    mg_platform_register_gem_slots();
    EXPECT_EQ(mg_gem_slots().count, after_first);

    const char* expected[] = {
        "gs_platform_setup",          "gs_platform_cleanup",
        "gs_platform_pump",           "gs_platform_sleep",
        "gs_platform_get_timestamp",  "gs_platform_get_datetime",
        "gs_platform_copy_file",      "gs_platform_delete_file",
        "gs_platform_move_file",      "gs_platform_file_exists",
        "gs_platform_dir_exists",     "gs_platform_read_dir",
        "gs_platform_read_txt_file",  "gs_platform_read_binary_file"};
    for (const char* name : expected) {
        const mg_gem_slot* slot = find_slot(name);
        ASSERT_NE(slot, nullptr) << name << " is not registered";
        // No gem loads in this process, so every slot still holds its default.
        EXPECT_EQ(*slot->ptr_address, slot->default_fn) << name;
    }

    /* Memory has no slots at all: the engine needs it before a gem is ever
     * loaded, so nothing about it is a gem's to fill. */
    EXPECT_EQ(find_slot("gs_platform_mem_alloc"), nullptr);
    EXPECT_EQ(find_slot("gs_mem_copy"), nullptr);
}

TEST(GemSlotRegistry, DefaultsAnswerWithoutAGem) {
    mg_platform_register_gem_slots();

    EXPECT_EQ(gs_platform_file_exists("no_such_file_anywhere"), FALSE);
    EXPECT_EQ(gs_platform_get_timestamp(), 0);
    EXPECT_EQ(gs_platform_pump(nullptr), FALSE);
}

TEST(GemSlotRegistry, RestoreFromDefaultFnPutsTheDefaultBack) {
    mg_register_gem_slot("test_fake_slot", (mg_lib_fn*)&fake_slot);
    const mg_gem_slot* slot = find_slot("test_fake_slot");
    ASSERT_NE(slot, nullptr);

    /* What bind and unbind do, without loading a library: point the slot
     * somewhere else, then put back what registration captured. */
    *slot->ptr_address = (mg_lib_fn)fake_replacement;
    EXPECT_NE((void*)fake_slot, (void*)fake_default);

    *slot->ptr_address = slot->default_fn;
    ASSERT_EQ((void*)fake_slot, (void*)fake_default);

    const int calls_before = g_fake_calls;
    fake_slot();
    EXPECT_EQ(g_fake_calls, calls_before + 1);
}

} // namespace

/**
 * @file tracked.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The counters behind memory/tracked.h, and the getters of
 * magic/memory/stats.h that read them.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "memory/tracked.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"

/* Process-wide, like the heap it describes: every allocation in the engine
 * funnels through the one mg_alloc, so there is one set of books, counting
 * before the app opens and after it closes -- which is why they are this
 * module's own and not the app's. A static is zeroed before main, which is
 * the correct opening balance. */
static mg_memory_usage g_usage;

void mg_track_alloc(const len size, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_IF((len)tag < MG_MEMORY_TAG_COUNT, "tag out of range");
    if (tag == MG_MEMORY_TAG_UNKNOWN)
        MG_WARN("Given unknown memory tag to track! Allocations should be properly tagged!");

    g_usage.tagged[tag].allocated += size;
    g_usage.total.allocated += size;
}

void mg_track_free(const len size, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_IF((len)tag < MG_MEMORY_TAG_COUNT, "tag out of range");
    if (tag == MG_MEMORY_TAG_UNKNOWN)
        MG_WARN("Given unknown memory tag to track! Frees should be properly tagged!");

    g_usage.tagged[tag].deallocated += size;
    g_usage.total.deallocated += size;
}

void mg_track_resize(const len old_size, const len new_size, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_IF((len)tag < MG_MEMORY_TAG_COUNT, "tag out of range");
    if (tag == MG_MEMORY_TAG_UNKNOWN)
        MG_WARN("Given unknown memory tag to track! Resizes should be properly tagged!");

    /* A resize is a free of the old bytes and an allocation of the new, booked
     * in one call: live usage stays allocated minus deallocated throughout. */
    g_usage.tagged[tag].allocated += new_size;
    g_usage.tagged[tag].deallocated += old_size;
    g_usage.total.allocated += new_size;
    g_usage.total.deallocated += old_size;
}

mg_memory_usage mg_get_mem_usage(void) {
    return g_usage;
}

mg_memory_stats mg_get_mem_usage_for(const enum mg_memory_tag tag) {
    const mg_memory_stats empty = {.allocated = 0, .deallocated = 0};
    MG_ASSERT_RETURN_VAL_IF((len)tag < MG_MEMORY_TAG_COUNT, empty, "tag out of range");

    return g_usage.tagged[tag];
}

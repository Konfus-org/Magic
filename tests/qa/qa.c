/**
 * @file qa.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The QA app: runs engine pieces end to end and reports what it saw.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/app.h"
#include "magic/memory/allocator.h"
#include <stdlib.h>

static len g_events = 0;
static len g_updates = 0;
static len g_fixed_updates = 0;

static ts g_print_stats_timer = 0;
static len g_total_frames = 0;
static ts g_total_time_ms = 0;

static len g_qa_allocs = 0;
static len g_qa_frees = 0;

/* An allocator that is libc's with a tally on the side: enough to prove the
 * engine really is allocating through what the app installed, and that
 * mg_on_init is early enough to install it. */

static void* qa_alloc(const len size) {
    g_qa_allocs++;
    return calloc(1, (size_t)size);
}

static void* qa_resize(void* ptr, const len new_size) {
    return realloc(ptr, (size_t)new_size);
}

static void qa_free(void* ptr) {
    if (ptr != NULL) {
        g_qa_frees++;
    }
    free(ptr);
}

static const mg_allocator g_qa_allocator = {
    .alloc = qa_alloc,
    .resize = qa_resize,
    .free = qa_free,
};

void mg_on_init(void) {
    /* The last moment this is allowed: mg_use_allocator refuses once anything
     * has been allocated, and nothing has yet. If that ever stops being true,
     * this call asserts rather than the heap coming apart later. */
    mg_use_allocator(&g_qa_allocator);
    MG_INFO("QA heard mg_on_init.");
}

void mg_on_start(void) {
    MG_INFO("QA heard mg_on_start.");
}

void mg_on_event(const void* sender, const mg_event* evt) {
    MG_UNUSED(sender);
    MG_UNUSED(evt);
    MG_INFO("QA heard mg_on_event: %d", evt->code);
    g_events++;
}

void mg_on_update(const ts dt) {
    if (g_updates == 0)
        MG_INFO("QA heard the first mg_on_update, dt %f ms.", dt);
    g_updates++;
    g_print_stats_timer += dt;
    g_total_frames++;
    g_total_time_ms += dt;

    /* The whole block is log output, so it is compiled in only where there is
     * a log to write to: with MG_INFO off, the usage it gathers would be a
     * store nothing loads. */
#if MG_INFO_ENABLED
    // Every second, print memory usage and average FPS.
    if (g_print_stats_timer >= 1000) {
        const mg_memory_usage usage = mg_get_mem_usage();
        MG_INFO("QA memory usage: %llu bytes", usage.total.allocated - usage.total.deallocated);
        MG_INFO("QA average FPS: %f", (ts)g_total_frames / (g_total_time_ms / 1000.0));
        g_print_stats_timer = 0;
    }
#endif
}

void mg_on_fixed_update(const ts fixed_dt) {
    MG_UNUSED(fixed_dt);
    g_fixed_updates++;
}

void mg_on_exit(void) {
    MG_INFO(
        "QA heard mg_on_exit after %llu updates, %llu fixed updates and %llu events.",
        g_updates,
        g_fixed_updates,
        g_events);
    MG_INFO(
        "QA saw %llu allocations and %llu frees through its own allocator.",
        g_qa_allocs,
        g_qa_frees);

    MG_ASSERT(g_updates > 0, "the loop never reached mg_on_update");
    MG_ASSERT(g_events > 0, "the platform's events never reached mg_on_event");
    MG_ASSERT(
        g_qa_allocs > 0,
        "the engine never allocated through the allocator mg_on_init installed");
}

/**
 * @file qa.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The QA app: runs engine pieces end to end and reports what it saw.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/app.h"
#include "magic/enums.h"
#include "magic/events/event.h"
#include "magic/memory/allocator.h"
#include "magic/memory/box.h"
#include <stdlib.h>

static len g_events = 0;
static len g_updates = 0;
static len g_fixed_updates = 0;

static ts g_print_stats_timer = 0;

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
    g_events++;

    /* Every code the platform boxes a payload for gets its values printed;
     * the rest print their name alone. On the enum values, not the enum type,
     * so a custom code past MG_EVENT_CODE_COUNT falls to default rather than
     * tripping -Wswitch-enum here. */
    const char* code_name = mg_get_enum_name(mg_event_code, evt->code);
    switch (evt->code) {
        case MG_EVENT_CODE_WINDOW_RESIZED:
        {
            const mg_window_resize_event* resize = mg_unbox(mg_window_resize_event, evt->data);
            if (resize != NULL) {
                MG_INFO("QA heard %s: %u x %u", code_name, resize->size.width, resize->size.height);
            }
            break;
        }
        case MG_EVENT_CODE_INPUT_PRESSED:
        case MG_EVENT_CODE_INPUT_RELEASED:
        {
            const mg_input_press_event* press = mg_unbox(mg_input_press_event, evt->data);
            if (press != NULL) {
                MG_INFO(
                    "QA heard %s: key %s, button %s",
                    code_name,
                    mg_get_enum_name(mg_key_code, press->key),
                    mg_get_enum_name(mg_button_code, press->button));
            }
            break;
        }
        case MG_EVENT_CODE_INPUT_DEVICE_CONNECTED:
        case MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED:
        {
            const mg_input_device_event* device = mg_unbox(mg_input_device_event, evt->data);
            if (device != NULL) {
                MG_INFO(
                    "QA heard %s: %s",
                    code_name,
                    mg_get_enum_name(mg_input_device_kind, device->kind));
            }
            break;
        }
        case MG_EVENT_CODE_INPUT_MOUSE_MOVED:
        {
            /* Trace, deliberately: the mouse fires many of these per frame,
             * and info would scroll everything else away. */
            const mg_mouse_move_event* move = mg_unbox(mg_mouse_move_event, evt->data);
            if (move != NULL) {
                MG_TRACE(
                    "QA heard %s: %.1f, %.1f (moved %.1f, %.1f)",
                    code_name,
                    (f64)move->x,
                    (f64)move->y,
                    (f64)move->delta_x,
                    (f64)move->delta_y);
            }
            break;
        }
        default:
            /* The number rides along: a custom code past MG_EVENT_CODE_COUNT
             * names as "?", and the number is all there is to go on. */
            MG_INFO("QA heard %s (%u)", code_name, evt->code);
            break;
    }
}

void mg_on_update(const ts dt) {
    if (g_updates == 0)
        MG_INFO("QA heard the first mg_on_update, dt %f ms.", dt);
    g_updates++;
    g_print_stats_timer += dt;

    /* The whole block is log output, so it is compiled in only where there is
     * a log to write to: with MG_INFO off, the usage it gathers would be a
     * store nothing loads. FPS is not printed here: the pump reports it, as
     * engine machinery for every app. */
#if MG_INFO_ENABLED
    // Every second, print memory usage.
    if (g_print_stats_timer >= 1000) {
        const mg_memory_usage usage = mg_get_mem_usage();
        MG_INFO("QA memory usage: %llu bytes", usage.total.allocated - usage.total.deallocated);
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

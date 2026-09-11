/**
 * @file slots.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The platform family's slot storage: every pointer starts at its
 * default and unbind puts the default back, so no slot is ever NULL and no
 * call site checks. A new slot adds its declare in magic/platform/slots.h,
 * and its default, define and register line here. Memory has no slots at
 * all: the engine needs it before a gem is ever loaded.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/platform/slots.h"
#include "magic/debug/asserts.h"
#include <stdlib.h>
#include <string.h>
#include <threads.h>

/* ========================================================================== */
/* Defaults, per concern. The trailing _ marks them private: they exist to    */
/* sit in a slot, so call the slot, never a default by name.                  */
/* ========================================================================== */

// Lifecycle

/**
 * @brief gs_platform_setup when no gem provides one: there is no
 * platform to set up, and false says so.
 */
static b8 mg_default_setup_platform_(mg_platform_state* initial_state) {
    (void)initial_state;
    return FALSE;
}

/**
 * @brief gs_platform_cleanup when no gem provides one: nothing was set
 * up, so nothing to shut down.
 */
static void mg_default_shutdown_platform_(void) {}

/**
 * @brief gs_platform_poll_event when no gem provides one: no platform,
 * no events, and false says none are pending.
 */
static b8 mg_default_poll_event_(mg_event* out_event) {
    (void)out_event;
    return FALSE;
}

/**
 * @brief gs_platform_pump when no gem provides one: with no platform
 * there is nothing to keep running, and false says the app should stop.
 */
static b8 mg_default_pump_(mg_platform_state* out_state) {
    (void)out_state;
    return FALSE;
}

/**
 * @brief gs_platform_update when no gem provides one: no platform,
 * nothing to apply the state to, and false says so.
 */
static b8 mg_default_update_(const mg_platform_state* state) {
    (void)state;
    return FALSE;
}

// Time

/**
 * @brief gs_platform_sleep when no gem provides one. Sub-millisecond
 * amounts round down; zero and negative amounts do not sleep at all.
 */
static void mg_default_sleep_(const ts ms) {
    if (ms <= 0) {
        return;
    }
    const u64 whole_ms = (u64)ms;
    const struct timespec dur = {
        .tv_sec = (time_t)(whole_ms / 1000u),
        .tv_nsec = (long)((whole_ms % 1000u) * 1000000u)};
    thrd_sleep(&dur, NULL);
}

/**
 * @brief gs_platform_get_timestamp when no gem provides one: a clock
 * that never ticks, so every delta measured against it is zero.
 */
static ts mg_default_get_timestamp_(void) {
    return 0;
}

/**
 * @brief gs_platform_get_datetime when no gem provides one: the zeroed
 * date, recognizably no date at all.
 */
static mg_datetime mg_default_get_datetime_(void) {
    const mg_datetime none = {0};
    return none;
}

/* Files. The engine has no portable filesystem of its own, so the defaults
 * answer as an empty one: nothing exists, nothing copies, every read comes
 * back empty. */

/**
 * @brief The two-path file operations when no gem provides them: with no
 * filesystem nothing copies or moves, and false says so.
 */
static b8 mg_default_file_pair_op_(const char* from, const char* to) {
    (void)from;
    (void)to;
    return FALSE;
}

/**
 * @brief The one-path file checks and operations when no gem provides them:
 * on a filesystem that does not exist, nothing exists and nothing deletes.
 */
static b8 mg_default_file_op_(const char* path) {
    (void)path;
    return FALSE;
}

/**
 * @brief gs_platform_read_dir when no gem provides one: the empty
 * directory listing; nothing to free beyond the empty list itself.
 */
static mg_str_list mg_default_read_dir_(const char* path) {
    (void)path;
    return mg_str_list_construct();
}

/**
 * @brief gs_platform_read_txt_file when no gem provides one: the empty
 * out-of-memory-shaped string every read failure hands back.
 */
static mg_str mg_default_read_txt_file_(const char* path) {
    (void)path;
    const mg_str none = {NULL, 0};
    return none;
}

/**
 * @brief gs_platform_read_binary_file when no gem provides one: the
 * empty byte list every read failure hands back.
 */
static mg_byte_list mg_default_read_binary_file_(const char* path) {
    (void)path;
    return mg_byte_list_construct();
}

/* ========================================================================== */
/* The slots, at their defaults                                               */
/* ========================================================================== */

// Lifecycle
MG_DEFINE_GEM_SLOT(
    b8,
    gs_platform_setup,
    mg_default_setup_platform_,
    mg_platform_state* initial_state);
MG_DEFINE_GEM_SLOT(void, gs_platform_cleanup, mg_default_shutdown_platform_, void);
MG_DEFINE_GEM_SLOT(b8, gs_platform_poll_event, mg_default_poll_event_, mg_event* out_event);
MG_DEFINE_GEM_SLOT(b8, gs_platform_pump, mg_default_pump_, mg_platform_state* out_state);
MG_DEFINE_GEM_SLOT(b8, gs_platform_update, mg_default_update_, const mg_platform_state* state);

// Time
MG_DEFINE_GEM_SLOT(void, gs_platform_sleep, mg_default_sleep_, ts ms);
MG_DEFINE_GEM_SLOT(ts, gs_platform_get_timestamp, mg_default_get_timestamp_, void);
MG_DEFINE_GEM_SLOT(mg_datetime, gs_platform_get_datetime, mg_default_get_datetime_, void);

// Files
MG_DEFINE_GEM_SLOT(
    b8,
    gs_platform_copy_file,
    mg_default_file_pair_op_,
    const char* from,
    const char* to);
MG_DEFINE_GEM_SLOT(b8, gs_platform_delete_file, mg_default_file_op_, const char* path);
MG_DEFINE_GEM_SLOT(
    b8,
    gs_platform_move_file,
    mg_default_file_pair_op_,
    const char* from,
    const char* to);
MG_DEFINE_GEM_SLOT(b8, gs_platform_file_exists, mg_default_file_op_, const char* path);
MG_DEFINE_GEM_SLOT(b8, gs_platform_dir_exists, mg_default_file_op_, const char* path);
MG_DEFINE_GEM_SLOT(mg_str_list, gs_platform_read_dir, mg_default_read_dir_, const char* path);
MG_DEFINE_GEM_SLOT(mg_str, gs_platform_read_txt_file, mg_default_read_txt_file_, const char* path);
MG_DEFINE_GEM_SLOT(
    mg_byte_list,
    gs_platform_read_binary_file,
    mg_default_read_binary_file_,
    const char* path);

void mg_platform_register_gem_slots(void) {
    // Lifecycle
    MG_REGISTER_GEM_SLOT(gs_platform_setup);
    MG_REGISTER_GEM_SLOT(gs_platform_cleanup);
    MG_REGISTER_GEM_SLOT(gs_platform_poll_event);
    MG_REGISTER_GEM_SLOT(gs_platform_pump);
    MG_REGISTER_GEM_SLOT(gs_platform_update);

    // Time
    MG_REGISTER_GEM_SLOT(gs_platform_sleep);
    MG_REGISTER_GEM_SLOT(gs_platform_get_timestamp);
    MG_REGISTER_GEM_SLOT(gs_platform_get_datetime);

    // Files
    MG_REGISTER_GEM_SLOT(gs_platform_copy_file);
    MG_REGISTER_GEM_SLOT(gs_platform_delete_file);
    MG_REGISTER_GEM_SLOT(gs_platform_move_file);
    MG_REGISTER_GEM_SLOT(gs_platform_file_exists);
    MG_REGISTER_GEM_SLOT(gs_platform_dir_exists);
    MG_REGISTER_GEM_SLOT(gs_platform_read_dir);
    MG_REGISTER_GEM_SLOT(gs_platform_read_txt_file);
    MG_REGISTER_GEM_SLOT(gs_platform_read_binary_file);
}

/**
 * @file slots.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Every slot of the platform backend, grouped per concern: lifecycle,
 * time, files. Memory is not among them -- the engine needs memory
 * before a gem is ever loaded, so nothing about it is a gem's to fill:
 * allocation is an app's to install (magic/memory/allocator.h) and the byte
 * moves are libc's (magic/memory/copy.h). Neither is logging: a backend that
 * wants the log registers itself with mg_register_logger
 * (magic/debug/logging.h) instead of filling a slot.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/collections/string.h"
#include "magic/events/event.h"
#include "magic/gems/slots.h"
#include "magic/platform/state.h"
#include "magic/primitives.h"
#include "magic/time/datetime.h"

/* Every slot holds a default until a gem fills it, so a call site never
 * checks for NULL: with no platform gem, setup reports no platform came up,
 * pump reports nothing left to run, and the filesystem is an empty one. The
 * defaults live in core/src/platform/slots.c; a new slot adds its declare
 * here, and its default, define and register line there. */

/**
 * @brief Registers every platform slot with the engine's gem registry.
 * mg_open_app calls it before loading gems; safe to call again.
 */
MG_EXPORT void mg_platform_register_gem_slots(void);

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/** @brief Sets up a platform with the given initial state. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_setup, mg_platform_state* initial_state);

/** @brief Shuts down the platform, releasing everything it holds. */
MG_DECLARE_GEM_SLOT(void, gs_platform_cleanup, void);

/**
 * @brief Hands out the next platform event that maps to an engine event,
 * translated into out_event. The engine polls until FALSE and queues each
 * into its own event registry; the platform never queues or fires itself, so
 * a backend swap never touches the mg event system.
 * @param out_event Receives the translated event when one came out.
 * @return TRUE when out_event holds an event; FALSE when none are pending.
 */
MG_DECLARE_GEM_SLOT(b8, gs_platform_poll_event, mg_event* out_event);

/**
 * @brief Pumps the platform: reports the current platform state and whether
 * the platform should keep running. The events themselves come out through
 * gs_platform_poll_event, which the engine drains first.
 * @param out_state Receives the platform state after the pump; may be NULL.
 * @return True while the platform should keep running; false is the quit.
 */
MG_DECLARE_GEM_SLOT(b8, gs_platform_pump, mg_platform_state* out_state);

/**
 * @brief Updates the platform from state the engine set: the pump's mirror,
 * pushing our state down where the pump reads the platform's out.
 * @param state The state to apply; the window takes its name, size and mode.
 * @return True when the platform applied it.
 */
MG_DECLARE_GEM_SLOT(b8, gs_platform_update, const mg_platform_state* state);

/* ========================================================================== */
/* Time                                                                       */
/* ========================================================================== */

/** @brief Sleeps the platform for the given number of milliseconds. */
MG_DECLARE_GEM_SLOT(void, gs_platform_sleep, ts ms);

/** @brief Gets the time the platform has been ticking, usually since its boot. */
MG_DECLARE_GEM_SLOT(ts, gs_platform_get_timestamp, void);

/** @brief Gets the current date/time. */
MG_DECLARE_GEM_SLOT(mg_datetime, gs_platform_get_datetime, void);

/* ========================================================================== */
/* Files                                                                      */
/* ========================================================================== */

/** @brief Copies the file at from to to, overwriting what is there. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_copy_file, const char* from, const char* to);

/** @brief Deletes a file on the platforms filesystem. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_delete_file, const char* path);

/** @brief Moves the file at from to to, overwriting what is there. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_move_file, const char* from, const char* to);

/** @brief Checks if a file exists on the platforms filesystem. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_file_exists, const char* path);

/** @brief Checks if a directory exists on the platforms filesystem. */
MG_DECLARE_GEM_SLOT(b8, gs_platform_dir_exists, const char* path);

/** @brief Reads a directory: the files and directories in it. */
MG_DECLARE_GEM_SLOT(mg_str_list, gs_platform_read_dir, const char* path);

/** @brief Reads a text file's contents. */
MG_DECLARE_GEM_SLOT(mg_str, gs_platform_read_txt_file, const char* path);

/** @brief Reads a binary file's contents. */
MG_DECLARE_GEM_SLOT(mg_byte_list, gs_platform_read_binary_file, const char* path);

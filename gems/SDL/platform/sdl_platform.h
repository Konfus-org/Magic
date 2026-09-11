/**
 * @file sdl_platform.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief What the SDL platform gem's pieces trade with each other: one
 * declaration per implementation, grouped per concern the way
 * magic/platform/slots.h groups the slots they fill. Each concern lives in
 * the .c named after it -- sdl_window.c, sdl_events.c, sdl_input.c,
 * sdl_time.c, sdl_files.c -- and sdl_platform_gem.c binds them into the one
 * registration table. Internal to the gem; nothing here is exported.
 *
 * SDL itself is not this gem's to bring up: SDL_base does that, and this gem
 * requires it. What is this gem's is the video and events subsystems, opened
 * in its on_load and closed in its on_unload.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/collections/string.h"
#include "magic/events/event.h"
#include "magic/platform/input_codes.h"
#include "magic/platform/state.h"
#include "magic/primitives.h"
#include "magic/time/datetime.h"
#include <SDL3/SDL.h>

/* ========================================================================== */
/* Windowing (sdl_window.c)                                                   */
/* ========================================================================== */

/**
 * @brief gs_platform_setup via SDL: opens the one window.
 * The window goes out in the state's platform data box, as the SDL_Window*
 * the render backend claims its swapchain on -- an SDL_Window* is vocabulary
 * both SDL gems already speak, so the handoff needs no shared struct, and no
 * shared header, between them. Boxed by address, so the box carries the
 * handle rather than the window: the reader dereferences once.
 */
b8 sdl_setup_platform(mg_platform_state* initial_state);

/** @brief gs_platform_cleanup via SDL. Safe to call when never set up. */
void sdl_shutdown_platform(void);

/** @brief gs_platform_pump via SDL: reports window and input state. */
b8 sdl_pump_platform(mg_platform_state* out_state);

/** @brief gs_platform_update via SDL: applies engine-set window state. */
b8 sdl_update_platform(const mg_platform_state* state);

/**
 * @brief Lowers the running flag the pump reports, so the platform stops
 * after the current frame. The event poll calls it on a quit.
 */
void sdl_stop_running(void);

/* ========================================================================== */
/* Events (sdl_events.c)                                                      */
/* ========================================================================== */

/** @brief gs_platform_poll_event via SDL. */
b8 sdl_poll_event(mg_event* out_event);

/* ========================================================================== */
/* Input (sdl_input.c)                                                        */
/* ========================================================================== */

/**
 * @brief Reads SDL's keyboard and mouse into out_input: the pump's input
 * half, called once per pump.
 * @param out_input Receives arrays over the gem's own storage, every code's
 * slot live; borrowed, and rewritten by the next gather.
 * @param screen_width The window width the mouse position packs against.
 */
void sdl_gather_input(mg_input_state* out_input, u32 screen_width);

/**
 * @brief Accumulates wheel scrolling for the next gather. The wheel only
 * speaks in events, so the window's event poll feeds it here.
 * @param sideways Horizontal steps this event carried.
 * @param steps Vertical steps, the usual wheel.
 */
void sdl_note_wheel(f32 sideways, f32 steps);

/**
 * @brief The engine key sitting where scancode does: the key map read the
 * other way, for translating key events.
 * @param scancode The SDL scancode to look up.
 * @return The key, or MG_KEY_NONE for a scancode no engine key names.
 */
mg_key_code sdl_key_from_scancode(SDL_Scancode scancode);

/**
 * @brief The engine button an SDL mouse button index names, for translating
 * button events.
 * @param button The index from an SDL mouse button event.
 * @return The button, or MG_BUTTON_NONE for an index no engine button names.
 */
mg_button_code sdl_button_from_mouse(u8 button);

/* ========================================================================== */
/* Time (sdl_time.c)                                                          */
/* ========================================================================== */

/** @brief gs_platform_sleep via SDL. */
void sdl_sleep(ts ms);

/** @brief gs_platform_get_timestamp via SDL. */
ts sdl_get_timestamp(void);

/** @brief gs_platform_get_datetime via SDL. */
mg_datetime sdl_get_datetime(void);

/* ========================================================================== */
/* Files (sdl_files.c)                                                        */
/* ========================================================================== */

/** @brief gs_platform_copy_file via SDL. */
b8 sdl_copy_file(const char* from, const char* to);

/** @brief gs_platform_delete_file via SDL. */
b8 sdl_delete_file(const char* path);

/** @brief gs_platform_move_file via SDL. */
b8 sdl_move_file(const char* from, const char* to);

/** @brief gs_platform_file_exists via SDL. */
b8 sdl_file_exists(const char* path);

/** @brief gs_platform_dir_exists via SDL. */
b8 sdl_dir_exists(const char* path);

/** @brief gs_platform_read_dir via SDL. */
mg_str_list sdl_read_dir(const char* path);

/** @brief gs_platform_read_txt_file via SDL. */
mg_str sdl_read_txt_file(const char* path);

/** @brief gs_platform_read_binary_file via SDL. */
mg_byte_list sdl_read_binary_file(const char* path);

/**
 * @file sdl_platform_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The SDL platform backend's identity: lifecycle hooks and the one
 * table binding the platform slots the gem fills. The implementations live
 * one concern per file -- sdl_window.c, sdl_events.c, sdl_input.c,
 * sdl_time.c, sdl_files.c -- all declared in sdl_platform.h.
 *
 * Requires SDL_base, which brings SDL up and wires it into the engine's
 * memory and log. This gem opens only the subsystems it needs on top of that,
 * and SDL refcounts them, so its quit is matched to its own init and leaves
 * the library standing for whatever else is still using it.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/gems/slots.h"
#include "magic/gems/state.h"
#include "magic/platform/slots.h"
#include "sdl_platform.h"
#include <SDL3/SDL.h>

/** @brief What this gem needs of SDL: a window to show and events to drain. */
#define SDL_PLATFORM_SUBSYSTEMS (SDL_INIT_VIDEO | SDL_INIT_EVENTS)

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it: opens the subsystems the
 * window and the event pump are drawn from. SDL is already up and already
 * routed through the engine by the time this runs -- that is what requiring
 * SDL_base buys, and why there is no SDL_Init here.
 * @param owner The app the gem was loaded into.
 */
static void sdl_platform_on_load(mg_app* owner) {
    MG_UNUSED(owner);

    if (!SDL_InitSubSystem(SDL_PLATFORM_SUBSYSTEMS)) {
        MG_ERROR("SDL video and events did not come up: %s", SDL_GetError());
    }
}

/**
 * @brief Tears the gem down when the engine unloads it.
 * @param owner The app the gem was loaded into.
 */
static void sdl_platform_on_unload(mg_app* owner) {
    MG_UNUSED(owner);

    /* Called directly, not through the slot: by unload time the loader may
     * have already put the slot back to its default. An unload without a
     * shutdown still closes the window this way. */
    sdl_shutdown_platform();

    /* Refcounted against this gem's own init, so this releases video and
     * events without touching SDL itself. SDL_base is still loaded -- the
     * loader unloads back to front -- and puts the library down after. */
    SDL_QuitSubSystem(SDL_PLATFORM_SUBSYSTEMS);
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "SDL_platform",
    "1.0",
    "SDL platform backend: window, events, input, time and files",
    "Jeremy Hummel",
    MG_GEM_REQUIRES("SDL_base"),
    sdl_platform_on_load,
    NULL,
    NULL,
    sdl_platform_on_unload)

MG_GEM_FILL_SLOTS(
    MG_GEM_FILL_SLOT(gs_platform_setup, sdl_setup_platform),
    MG_GEM_FILL_SLOT(gs_platform_cleanup, sdl_shutdown_platform),
    MG_GEM_FILL_SLOT(gs_platform_poll_event, sdl_poll_event),
    MG_GEM_FILL_SLOT(gs_platform_pump, sdl_pump_platform),
    MG_GEM_FILL_SLOT(gs_platform_update, sdl_update_platform),
    MG_GEM_FILL_SLOT(gs_platform_sleep, sdl_sleep),
    MG_GEM_FILL_SLOT(gs_platform_get_timestamp, sdl_get_timestamp),
    MG_GEM_FILL_SLOT(gs_platform_get_datetime, sdl_get_datetime),
    MG_GEM_FILL_SLOT(gs_platform_copy_file, sdl_copy_file),
    MG_GEM_FILL_SLOT(gs_platform_delete_file, sdl_delete_file),
    MG_GEM_FILL_SLOT(gs_platform_move_file, sdl_move_file),
    MG_GEM_FILL_SLOT(gs_platform_file_exists, sdl_file_exists),
    MG_GEM_FILL_SLOT(gs_platform_dir_exists, sdl_dir_exists),
    MG_GEM_FILL_SLOT(gs_platform_read_dir, sdl_read_dir),
    MG_GEM_FILL_SLOT(gs_platform_read_txt_file, sdl_read_txt_file),
    MG_GEM_FILL_SLOT(gs_platform_read_binary_file, sdl_read_binary_file))

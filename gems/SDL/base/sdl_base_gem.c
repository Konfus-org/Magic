/**
 * @file sdl_base_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The SDL base gem's identity: the one place SDL is brought up and put
 * back down, and where it is wired into the engine's memory and its log. The
 * implementations live one concern per file -- sdl_memory.c, sdl_logging.c --
 * both declared in sdl_base.h.
 *
 * Fills no slot. What this gem provides is a state, not a function: an SDL
 * initialized, allocating through mg_alloc and logging through MG_*, ready
 * for SDL_platform and SDL_gpu to open subsystems on. Both name it with
 * MG_GEM_REQUIRES, which is what puts this on_load ahead of theirs and this
 * on_unload behind them.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/gems/state.h"
#include "sdl_base.h"
#include <SDL3/SDL.h>

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it: SDL's allocator first,
 * while SDL has yet to allocate anything for the redirect to orphan, then its
 * log, then SDL itself.
 * @param owner The app the gem was loaded into.
 */
static void sdl_base_on_load(mg_app* owner) {
    MG_UNUSED(owner);

    sdl_route_memory_to_magic();
    sdl_route_logging_to_magic();

    /* No subsystem flags: this gem brings the library up and nothing more.
     * Which subsystems run is for the gems built on it to say -- SDL_platform
     * asks for video and events, SDL_gpu for what it draws through -- and
     * SDL refcounts each, so every one of them is matched by its own quit. */
    if (!SDL_Init(0)) {
        MG_ERROR("SDL did not initialize: %s", SDL_GetError());
    }
}

/**
 * @brief Tears the gem down when the engine unloads it. Last of the SDL gems
 * to get here: the loader unloads back to front, so every gem that required
 * this one has already quit its subsystems.
 * @param owner The app the gem was loaded into.
 */
static void sdl_base_on_unload(mg_app* owner) {
    MG_UNUSED(owner);

    SDL_Quit();

    /* After the quit, so anything SDL says on its way out is still heard, and
     * before this image goes, so nothing SDL says afterwards calls into it. */
    sdl_restore_logging();
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "SDL_base",
    "1.0",
    "SDL brought up, allocating and logging through the engine",
    "Jeremy Hummel",
    MG_GEM_REQUIRES_NOTHING,
    sdl_base_on_load,
    NULL,
    NULL,
    sdl_base_on_unload)

/* No MG_GEM_FILL_SLOTS block: this gem fills no slot. */

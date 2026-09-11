/**
 * @file sdl_gpu_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The SDL render backend's identity: lifecycle hooks and the one table
 * binding the render slots the gem fills. The implementations live one
 * concern per file -- sdl_gpu_device.c, sdl_gpu_frame.c -- both declared in
 * sdl_gpu.h.
 *
 * Requires SDL_base, which brings SDL up and wires it into the engine's
 * memory and log. Video is asked for here and not left to SDL_platform: a
 * window is what SDL_GPU claims onto, so this gem needs the subsystem whether
 * or not another gem happens to have opened it. SDL refcounts it, so both
 * asking is two matched pairs rather than a conflict.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/gems/slots.h"
#include "magic/gems/state.h"
#include "magic/rendering/slots.h"
#include "sdl_gpu.h"
#include <SDL3/SDL.h>

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it. The device waits for
 * gs_render_backend_init, which is where the engine says which backend it
 * wants and hands over the window; all this can do beforehand is make sure
 * there is a video subsystem for that window to have come from.
 * @param owner The app the gem was loaded into.
 */
static void sdl_gpu_on_load(mg_app* owner) {
    MG_UNUSED(owner);

    if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) {
        MG_ERROR("SDL video did not come up, so SDL_gpu has nothing to draw on: %s", SDL_GetError());
    }
}

/**
 * @brief Tears the gem down when the engine unloads it.
 * @param owner The app the gem was loaded into.
 */
static void sdl_gpu_on_unload(mg_app* owner) {
    MG_UNUSED(owner);

    /* Called directly, not through the slot: by unload time the loader may
     * have already put the slot back to its default. An unload without a
     * shutdown still releases the device this way. */
    sdl_shutdown_render_backend();

    SDL_QuitSubSystem(SDL_INIT_VIDEO);
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "SDL_gpu",
    "1.0",
    "SDL_GPU render backend",
    "Jeremy Hummel",
    MG_GEM_REQUIRES("SDL_base"),
    sdl_gpu_on_load,
    NULL,
    NULL,
    sdl_gpu_on_unload)

MG_GEM_FILL_SLOTS(
    MG_GEM_FILL_SLOT(gs_render_backend_init, sdl_init_render_backend),
    MG_GEM_FILL_SLOT(gs_render_backend_shutdown, sdl_shutdown_render_backend),
    MG_GEM_FILL_SLOT(gs_render_backend_begin_frame, sdl_begin_frame),
    MG_GEM_FILL_SLOT(gs_render_backend_draw_frame, sdl_draw_frame),
    MG_GEM_FILL_SLOT(gs_render_backend_end_frame, sdl_end_frame))

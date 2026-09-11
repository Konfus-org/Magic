/**
 * @file sdl_window.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Windowing: the one window this backend drives, and the lifecycle
 * slots around it -- setup, cleanup, the pump and the update. The event poll
 * lives in sdl_events.c, and lowers the running flag here on a quit. SDL
 * itself is up before any of this runs: SDL_base brings the library up and
 * this gem's on_load opens the video subsystem, so nothing here inits or
 * quits SDL.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/logging.h"
#include "sdl_platform.h"
#include <SDL3/SDL.h>

/** @brief What the window opens as when the initial state does not say. */
#define SDL_GEM_DEFAULT_TITLE "Magic"
#define SDL_GEM_DEFAULT_WIDTH 1280
#define SDL_GEM_DEFAULT_HEIGHT 720

/** @brief The one window this backend drives; NULL until setup succeeds. */
static SDL_Window* g_window = NULL;

/** @brief Whether the platform should keep running; quit events clear it. */
static b8 g_running = TRUE;

/**
 * @brief gs_platform_setup via SDL.
 * @param initial_state The state to set the platform up with; the window
 * takes its title from window.title, falling back to the app's name.
 * @return True on success.
 */
b8 sdl_setup_platform(mg_platform_state* initial_state) {
    if (g_window != NULL) {
        MG_WARN("SDL platform set up twice; ignoring the second setup.");
        return TRUE;
    }

    /* The gem's on_load opened it, and the loader only gets here once
     * SDL_base has had its own on_load, so a closed video subsystem means the
     * gem came up wrong rather than a window that has yet to be asked for. */
    if (!SDL_WasInit(SDL_INIT_VIDEO)) {
        MG_ERROR("SDL video is not up, so no window can open: %s", SDL_GetError());
        return FALSE;
    }

    mg_str_view title = mg_view_cstr(SDL_GEM_DEFAULT_TITLE);
    i32 width = SDL_GEM_DEFAULT_WIDTH;
    i32 height = SDL_GEM_DEFAULT_HEIGHT;
    if (initial_state != NULL) {
        if (!mg_str_view_empty(initial_state->window.title)) {
            title = initial_state->window.title;
        } else if (!mg_str_view_empty(initial_state->name)) {
            title = initial_state->name;
        }
        if (initial_state->window.window_size.width > 0
            && initial_state->window.window_size.height > 0) {
            width = (i32)initial_state->window.window_size.width;
            height = (i32)initial_state->window.window_size.height;
        }
    }

    g_window = SDL_CreateWindow(mg_str_view_to_cstr(title), width, height, SDL_WINDOW_RESIZABLE);
    if (g_window == NULL) {
        MG_ERROR("SDL_CreateWindow failed: %s", SDL_GetError());
        return FALSE;
    }

    /* The window goes out the way it comes back in through the pump, so the
     * render backend finds it in the state whichever of the two handed it
     * over. Boxed by address: the box carries the handle, and a later
     * SDL_CreateWindow writing g_window is seen through the same box. */
    if (initial_state != NULL) {
        initial_state->data = mg_boxup(&g_window);
    }

    g_running = TRUE;
    return TRUE;
}

void sdl_shutdown_platform(void) {
    if (g_window != NULL) {
        SDL_DestroyWindow(g_window);
        g_window = NULL;
    }
    /* No SDL_Quit: the library is SDL_base's to put down, and the video
     * subsystem this gem opened is quit by its own on_unload. */
}

/**
 * @brief gs_platform_pump via SDL: reports the window, the screen and the
 * input state, and whether the platform should keep running. The events
 * themselves come out through sdl_poll_event, which the engine drains first.
 * @param out_state Receives the platform state after the pump.
 * @return True while the platform should keep running.
 */
b8 sdl_pump_platform(mg_platform_state* out_state) {
    if (out_state != NULL && g_window != NULL) {
        i32 width = 0;
        i32 height = 0;
        SDL_GetWindowSize(g_window, &width, &height);
        out_state->name = mg_view_cstr(SDL_GetPlatform());
        out_state->window.title = mg_view_cstr(SDL_GetWindowTitle(g_window));
        out_state->window.window_size.width = (u32)width;
        out_state->window.window_size.height = (u32)height;
        /* Mode 0 is windowed; anything else is fullscreen, until modes get
         * names. The update below reads the same encoding. */
        out_state->window.window_mode =
            (SDL_GetWindowFlags(g_window) & SDL_WINDOW_FULLSCREEN) != 0 ? 1 : 0;
        out_state->data = mg_boxup(&g_window);
        sdl_gather_input(&out_state->input, (u32)width);
    }

    return g_running;
}

/**
 * @brief gs_platform_update via SDL: applies engine-set state to the live
 * window, the pump's mirror.
 * @param state The state to apply; NULL applies nothing.
 * @return True when a window was up to take it.
 */
b8 sdl_update_platform(const mg_platform_state* state) {
    if (g_window == NULL || state == NULL) {
        return FALSE;
    }

    if (!mg_str_view_empty(state->window.title)) {
        SDL_SetWindowTitle(g_window, mg_str_view_to_cstr(state->window.title));
    }
    if (state->window.window_size.width > 0 && state->window.window_size.height > 0) {
        SDL_SetWindowSize(
            g_window,
            (i32)state->window.window_size.width,
            (i32)state->window.window_size.height);
    }
    SDL_SetWindowFullscreen(g_window, state->window.window_mode != 0);
    return TRUE;
}

void sdl_stop_running(void) {
    g_running = FALSE;
}

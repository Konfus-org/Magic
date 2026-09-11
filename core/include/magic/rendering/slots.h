/**
 * @file slots.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Every slot of the render backend: init and shutdown around the
 * frame cycle -- begin, draw, end.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/gems/slots.h"
#include "magic/platform/state.h"
#include "magic/primitives.h"
#include "magic/rendering/renderer.h"

/* Every slot holds a default until a gem fills it, so a call site never
 * checks for NULL: with no render backend gem, init reports no backend came
 * up and begin frame answers false, so no frame is ever drawn. The defaults
 * live in core/src/rendering/slots.c; a new slot adds its declare here, and
 * its default, define and register line there. */

/**
 * @brief Registers every render backend slot with the engine's gem registry.
 * mg_open_app calls it before loading gems; safe to call again.
 */
MG_EXPORT void mg_render_backend_register_gem_slots(void);

/**
 * @brief Sets up the render backend against the platform: the window it
 * draws to lives in the state's platform-specific data.
 * @param backend The backend asked for; a gem that cannot speak it says no.
 * @param platform_state The platform as it stands, window included.
 * @return TRUE when the backend came up and frames may begin.
 */
MG_DECLARE_GEM_SLOT(
    b8,
    gs_render_backend_init,
    mg_renderer_api backend,
    const mg_platform_state* platform_state);

/** @brief Shuts down the render backend, releasing everything it holds. */
MG_DECLARE_GEM_SLOT(void, gs_render_backend_shutdown, void);

/**
 * @brief Opens a frame to draw into, given the platform as it stands --
 * whether a frame is worth opening is the backend's read of it: a minimized
 * window or an out-of-date swapchain says skip.
 * @param platform_state The platform as it stands, window size included.
 * @return TRUE when the frame is open and draw may proceed; FALSE when this
 * frame should not be drawn, in which case neither draw nor end is called.
 */
MG_DECLARE_GEM_SLOT(
    b8,
    gs_render_backend_begin_frame,
    const mg_platform_state* platform_state);

/**
 * @brief Draws into the frame begin opened. Only between a begin that
 * answered TRUE and its end.
 * @param delta_time Milliseconds since the last frame, for anything animated.
 */
MG_DECLARE_GEM_SLOT(void, gs_render_backend_draw_frame, ts delta_time);

/** @brief Closes the frame begin opened and presents it. */
MG_DECLARE_GEM_SLOT(void, gs_render_backend_end_frame, void);

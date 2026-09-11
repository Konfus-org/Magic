/**
 * @file renderer.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/rendering/renderer.h: each call forwards to
 * the render backend slots, so a backend swap never touches a call site.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/rendering/renderer.h"
#include "magic/app.h"
#include "magic/rendering/slots.h"

b8 mg_renderer_init(const mg_renderer_api api) {
    return gs_render_backend_init(api, &mg_get_app_instance()->platform);
}

void mg_renderer_draw(const ts delta_time, const mg_platform_state* platform_state) {
    /* A false begin is the backend saying skip -- a minimized window, an
     * out-of-date swapchain -- so neither draw nor end runs this frame. */
    if (!gs_render_backend_begin_frame(platform_state))
        return;

    gs_render_backend_draw_frame(delta_time);
    gs_render_backend_end_frame();
}

void mg_renderer_shutdown(void) {
    gs_render_backend_shutdown();
}

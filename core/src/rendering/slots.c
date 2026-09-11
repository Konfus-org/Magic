/**
 * @file slots.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The render backend family's slot storage: every pointer starts at
 * its default and unbind puts the default back, so no slot is ever NULL and
 * no call site checks. A new slot adds its declare in
 * magic/rendering/slots.h, and its default, define and register line here.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/rendering/slots.h"
#include "magic/debug/asserts.h"

/* ========================================================================== */
/* Defaults. Static, and private for it: they exist to sit in a slot, so call */
/* the slot, never a default by name.                                         */
/* ========================================================================== */

/**
 * @brief gs_render_backend_init when no gem provides one: there is no
 * backend to set up, and false says so.
 */
static b8 mg_default_init_render_backend(
    const mg_renderer_api backend,
    const mg_platform_state* platform_state) {
    MG_UNUSED(backend, platform_state);
    return FALSE;
}

/**
 * @brief gs_render_backend_shutdown when no gem provides one: nothing was
 * set up, so nothing to shut down.
 */
static void mg_default_shutdown_render_backend(void) {}

/**
 * @brief gs_render_backend_begin_frame when no gem provides one: with no
 * backend there is nothing to draw with, and false skips every frame.
 */
static b8 mg_default_begin_frame(const mg_platform_state* platform_state) {
    MG_UNUSED(platform_state);
    return FALSE;
}

/**
 * @brief gs_render_backend_draw_frame when no gem provides one: unreachable
 * through a begin that always answers false, and a no-op if called anyway.
 */
static void mg_default_draw_frame(const ts delta_time) {
    MG_UNUSED(delta_time);
}

/**
 * @brief gs_render_backend_end_frame when no gem provides one: no frame was
 * ever opened, so nothing to close or present.
 */
static void mg_default_end_frame(void) {}

/* ========================================================================== */
/* The slots, at their defaults                                               */
/* ========================================================================== */

MG_DEFINE_GEM_SLOT(
    b8,
    gs_render_backend_init,
    mg_default_init_render_backend,
    mg_renderer_api backend,
    const mg_platform_state* platform_state);
MG_DEFINE_GEM_SLOT(void, gs_render_backend_shutdown, mg_default_shutdown_render_backend, void);
MG_DEFINE_GEM_SLOT(
    b8,
    gs_render_backend_begin_frame,
    mg_default_begin_frame,
    const mg_platform_state* platform_state);
MG_DEFINE_GEM_SLOT(void, gs_render_backend_draw_frame, mg_default_draw_frame, ts delta_time);
MG_DEFINE_GEM_SLOT(void, gs_render_backend_end_frame, mg_default_end_frame, void);

void mg_render_backend_register_gem_slots(void) {
    MG_REGISTER_GEM_SLOT(gs_render_backend_init);
    MG_REGISTER_GEM_SLOT(gs_render_backend_shutdown);
    MG_REGISTER_GEM_SLOT(gs_render_backend_begin_frame);
    MG_REGISTER_GEM_SLOT(gs_render_backend_draw_frame);
    MG_REGISTER_GEM_SLOT(gs_render_backend_end_frame);
}

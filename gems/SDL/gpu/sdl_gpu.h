/**
 * @file sdl_gpu.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief What the SDL gpu gem's pieces trade with each other: one declaration
 * per implementation, grouped per concern the way magic/rendering/slots.h
 * groups the slots they fill. Each concern lives in the .c named after it --
 * sdl_gpu_device.c, sdl_gpu_frame.c -- and sdl_gpu_gem.c binds them into the
 * one registration table. Internal to the gem; nothing here is exported.
 *
 * Requires SDL_base, which brings SDL up and wires it into the engine's
 * memory and log. The window this draws to is not this gem's to open: it
 * arrives in the platform state's data box, put there by whichever gem filled
 * the platform slots.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/platform/state.h"
#include "magic/primitives.h"
#include "magic/rendering/renderer.h"
#include <SDL3/SDL.h>

/* ========================================================================== */
/* Device (sdl_gpu_device.c)                                                  */
/* ========================================================================== */

/** @brief gs_render_backend_init via SDL_GPU: the device, and the window claimed. */
b8 sdl_init_render_backend(mg_renderer_api backend, const mg_platform_state* platform_state);

/** @brief gs_render_backend_shutdown via SDL_GPU. Safe when never initialized. */
void sdl_shutdown_render_backend(void);

/** @brief The device the frame cycle records into; NULL until init succeeds. */
SDL_GPUDevice* sdl_gpu_device(void);

/** @brief The claimed window frames are presented to; NULL until init succeeds. */
SDL_Window* sdl_gpu_window(void);

/* ========================================================================== */
/* Frames (sdl_gpu_frame.c)                                                   */
/* ========================================================================== */

/** @brief gs_render_backend_begin_frame via SDL_GPU. */
b8 sdl_begin_frame(const mg_platform_state* platform_state);

/** @brief gs_render_backend_draw_frame via SDL_GPU. */
void sdl_draw_frame(ts delta_time);

/** @brief gs_render_backend_end_frame via SDL_GPU: submits and presents. */
void sdl_end_frame(void);

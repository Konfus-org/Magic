/**
 * @file renderer.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The renderer the engine calls: init, draw, shutdown. Each forwards
 * to the render backend slots in magic/rendering/slots.h, which a gem fills.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/platform/state.h"
#include "magic/primitives.h"

typedef enum mg_renderer_api {
    MG_RENDERER_BACKEND_NONE,
    MG_RENDERER_BACKEND_OPENGL,
    MG_RENDERER_BACKEND_VULKAN,
    MG_RENDERER_BACKEND_D3D11,
    MG_RENDERER_BACKEND_D3D12,
    MG_RENDERER_BACKEND_METAL,
} mg_renderer_api;

/** @brief Initializes the renderer using the specified api. */
MG_EXPORT b8 mg_renderer_init(mg_renderer_api api);

/** @brief Draws one frame. */
MG_EXPORT void mg_renderer_draw(ts delta_time, const mg_platform_state* platform_state);

/** @brief Cleans up any renderer resources allocated by the renderer. */
MG_EXPORT void mg_renderer_shutdown(void);

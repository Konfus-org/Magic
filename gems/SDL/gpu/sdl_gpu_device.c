/**
 * @file sdl_gpu_device.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The device: SDL_GPU brought up against the window the platform
 * opened, and put back down. Everything here is once-per-run; the per-frame
 * half lives in sdl_gpu_frame.c.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/logging.h"
#include "magic/memory/box.h"
#include "sdl_gpu.h"
#include <SDL3/SDL.h>

/**
 * @brief Every shader form this gem will take. SDL picks the one the driver
 * it chose actually speaks, so asking for all three is asking for whichever
 * fits rather than for three separate things.
 */
#define SDL_GPU_SHADER_FORMATS                                                                     \
    (SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPU_SHADERFORMAT_DXIL | SDL_GPU_SHADERFORMAT_MSL)

/** @brief What a frame with nothing drawn into it clears to. */
#define SDL_GPU_CLEAR_COLOR                                                                        \
    (LITERAL(SDL_FColor) {.r = 0.05f, .g = 0.05f, .b = 0.08f, .a = 1.0f})

/** @brief The device this gem records through; NULL until init succeeds. */
static SDL_GPUDevice* g_device = NULL;

/** @brief The window claimed for that device. Borrowed from the platform gem. */
static SDL_Window* g_window = NULL;

/**
 * @brief The SDL driver name an engine backend asks for.
 * @param backend The backend the engine wants.
 * @param out_name Receives the driver name, or NULL for SDL's own pick.
 * @return FALSE for a backend SDL_GPU does not speak, which is this gem
 * saying no rather than quietly drawing through something else.
 */
static b8 sdl_gpu_driver_name(const mg_renderer_api backend, const char** out_name) {
    *out_name = NULL;
    switch (backend) {
        case MG_RENDERER_BACKEND_NONE:
            // Nothing named, so whatever SDL finds best on this machine.
            return TRUE;
        case MG_RENDERER_BACKEND_VULKAN:
            *out_name = "vulkan";
            return TRUE;
        case MG_RENDERER_BACKEND_D3D12:
            *out_name = "direct3d12";
            return TRUE;
        case MG_RENDERER_BACKEND_METAL:
            *out_name = "metal";
            return TRUE;
        case MG_RENDERER_BACKEND_OPENGL:
        case MG_RENDERER_BACKEND_D3D11:
        default:
            return FALSE;
    }
}

/**
 * @brief The window the platform gem put in the state's data box.
 * Boxed by address, so the box carries the handle: one dereference, and a
 * size check first, because the box is untyped and a platform gem that is not
 * an SDL one will have put something else entirely in it.
 * @param platform_state The state the engine handed the init.
 * @return The window, or NULL when the state carries none.
 */
static SDL_Window* sdl_window_from_platform(const mg_platform_state* platform_state) {
    if (platform_state == NULL || platform_state->data.data == NULL
        || platform_state->data.size != sizeof(SDL_Window*)) {
        return NULL;
    }
    return *mg_unbox(SDL_Window*, platform_state->data);
}

b8 sdl_init_render_backend(
    const mg_renderer_api backend,
    const mg_platform_state* platform_state) {
    if (g_device != NULL) {
        MG_WARN("SDL gpu backend initialized twice; ignoring the second.");
        return TRUE;
    }

    const char* driver = NULL;
    if (!sdl_gpu_driver_name(backend, &driver)) {
        MG_ERROR("SDL_gpu does not speak renderer backend %d.", (int)backend);
        return FALSE;
    }

    SDL_Window* window = sdl_window_from_platform(platform_state);
    if (window == NULL) {
        MG_ERROR("SDL_gpu has no window to draw to: the platform state carries none.");
        return FALSE;
    }

    /* DEBUG_MAGIC is the build flavour every gem is compiled with, so the
     * validation layers ride along exactly where the asserts and the logs
     * already do. */
#if defined(DEBUG_MAGIC)
    const bool debug_mode = true;
#else
    const bool debug_mode = false;
#endif

    g_device = SDL_CreateGPUDevice(SDL_GPU_SHADER_FORMATS, debug_mode, driver);
    if (g_device == NULL) {
        MG_ERROR(
            "SDL_gpu could not create a device for '%s': %s",
            driver != NULL ? driver : "the default driver",
            SDL_GetError());
        return FALSE;
    }

    if (!SDL_ClaimWindowForGPUDevice(g_device, window)) {
        MG_ERROR("SDL_gpu could not claim the window: %s", SDL_GetError());
        SDL_DestroyGPUDevice(g_device);
        g_device = NULL;
        return FALSE;
    }

    g_window = window;
    MG_INFO("SDL_gpu is up on '%s'.", SDL_GetGPUDeviceDriver(g_device));
    return TRUE;
}

void sdl_shutdown_render_backend(void) {
    if (g_device == NULL) {
        return;
    }

    /* Released before the device goes, and only if the window is still there:
     * the platform gem may have destroyed it already, and releasing a window
     * that is gone is a call into freed memory. Unload order keeps that from
     * happening -- SDL_gpu and SDL_platform are siblings, so neither waits on
     * the other -- which is why the window is checked rather than assumed. */
    if (g_window != NULL) {
        SDL_ReleaseWindowFromGPUDevice(g_device, g_window);
        g_window = NULL;
    }

    SDL_DestroyGPUDevice(g_device);
    g_device = NULL;
}

SDL_GPUDevice* sdl_gpu_device(void) {
    return g_device;
}

SDL_Window* sdl_gpu_window(void) {
    return g_window;
}

/**
 * @file sdl_gpu_frame.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The frame cycle: the command buffer and swapchain texture one frame
 * is recorded into, between a begin that answered TRUE and its end. The
 * device they are drawn from lives in sdl_gpu_device.c.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "sdl_gpu.h"
#include <SDL3/SDL.h>

/** @brief What a frame with nothing drawn into it clears to. */
#define SDL_GPU_CLEAR_COLOR                                                                        \
    (LITERAL(SDL_FColor) {.r = 0.05f, .g = 0.05f, .b = 0.08f, .a = 1.0f})

/* The frame in flight: both NULL between frames, both live between a TRUE
 * begin and its end. Nothing here outlives a frame, so there is nothing for
 * the shutdown to release. */
static SDL_GPUCommandBuffer* g_commands = NULL;
static SDL_GPUTexture* g_swapchain = NULL;

/** @brief Gives the command buffer back unrecorded, for a frame not worth opening. */
static void sdl_abandon_frame(void) {
    if (g_commands != NULL) {
        SDL_CancelGPUCommandBuffer(g_commands);
        g_commands = NULL;
    }
    g_swapchain = NULL;
}

b8 sdl_begin_frame(const mg_platform_state* platform_state) {
    /* The size the engine reports is the window's, and the swapchain's is the
     * driver's answer about the same window; the acquire below is the one
     * that decides, so the state is not read here. */
    MG_UNUSED(platform_state);

    SDL_GPUDevice* device = sdl_gpu_device();
    SDL_Window* window = sdl_gpu_window();
    if (device == NULL || window == NULL) {
        return FALSE;
    }

    g_commands = SDL_AcquireGPUCommandBuffer(device);
    if (g_commands == NULL) {
        MG_ERROR("SDL_gpu could not acquire a command buffer: %s", SDL_GetError());
        return FALSE;
    }

    /* Waits for the swapchain rather than taking whatever is ready, which is
     * what paces the engine to the display instead of to a spin. */
    u32 width = 0;
    u32 height = 0;
    if (!SDL_WaitAndAcquireGPUSwapchainTexture(g_commands, window, &g_swapchain, &width, &height)) {
        MG_ERROR("SDL_gpu could not acquire a swapchain texture: %s", SDL_GetError());
        sdl_abandon_frame();
        return FALSE;
    }

    /* No texture and no error is the driver saying not this frame -- a
     * minimized window, a swapchain being rebuilt. Not a failure, so nothing
     * is logged; the frame is simply skipped, and neither draw nor end runs. */
    if (g_swapchain == NULL) {
        sdl_abandon_frame();
        return FALSE;
    }

    return TRUE;
}

void sdl_draw_frame(const ts delta_time) {
    // Nothing animates yet: the pass below clears and draws nothing into it.
    MG_UNUSED(delta_time);

    MG_ASSERT_RETURN_IF(
        g_commands != NULL && g_swapchain != NULL,
        "a draw only follows a begin that answered TRUE");

    /* One pass that clears and stores. It is what makes the frame a frame
     * until there is geometry to put in it: without a pass the swapchain
     * texture is never written, and presenting an untouched one is undefined. */
    SDL_GPUColorTargetInfo target = {0};
    target.texture = g_swapchain;
    target.clear_color = SDL_GPU_CLEAR_COLOR;
    target.load_op = SDL_GPU_LOADOP_CLEAR;
    target.store_op = SDL_GPU_STOREOP_STORE;

    SDL_GPURenderPass* pass = SDL_BeginGPURenderPass(g_commands, &target, 1, NULL);
    if (pass == NULL) {
        MG_ERROR("SDL_gpu could not begin a render pass: %s", SDL_GetError());
        return;
    }
    SDL_EndGPURenderPass(pass);
}

void sdl_end_frame(void) {
    MG_ASSERT_RETURN_IF(g_commands != NULL, "an end only follows a begin that answered TRUE");

    // The submit presents: the swapchain texture was acquired for this window.
    if (!SDL_SubmitGPUCommandBuffer(g_commands)) {
        MG_ERROR("SDL_gpu could not submit the frame: %s", SDL_GetError());
    }

    g_commands = NULL;
    g_swapchain = NULL;
}

/**
 * @file sdl_memory.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Memory: SDL's own allocations routed UP into mg_alloc, tagged
 * PLATFORM, via SDL_SetMemoryFunctions, so SDL's internal usage shows in the
 * tag ledger. One direction only -- the engine's allocator is an app's to
 * install and defaults to libc, never to SDL -- so there is no cycle to break.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/logging.h"
#include "magic/memory/alloc.h"
#include "sdl_base.h"
#include <SDL3/SDL.h>

/**
 * @brief SDL_malloc as the engine's malloc: routes into mg_alloc, so SDL's
 * internal allocations land on the tagged heap. SDL never passes a zero size.
 * size_t rather than len here and below: the signatures must match SDL's
 * SDL_malloc_func family exactly.
 * @param size How many bytes SDL asked for.
 * @return The allocation, or NULL on failure.
 */
static void* SDLCALL sdl_mg_malloc(const size_t size) {
    return mg_alloc(1, size > 0 ? size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_calloc into mg_alloc, which already zeroes.
 * @param nmemb How many items SDL asked for.
 * @param size Bytes per item.
 * @return The zeroed allocation, or NULL on failure.
 */
static void* SDLCALL sdl_mg_calloc(const size_t nmemb, const size_t size) {
    return mg_alloc(nmemb > 0 ? nmemb : 1, size > 0 ? size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_realloc into mg_resize, which keeps realloc semantics.
 * @param mem The block to resize; NULL allocates.
 * @param size The new byte count.
 * @return The possibly-moved block, or NULL on failure with mem untouched.
 */
static void* SDLCALL sdl_mg_realloc(void* mem, const size_t size) {
    return mg_resize(mem, 1, size > 0 ? size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_free into mg_free. NULL is a no-op on both sides.
 * @param mem The block to release.
 */
static void SDLCALL sdl_mg_free(void* mem) {
    mg_free(mem, MG_MEMORY_TAG_PLATFORM);
}

void sdl_route_memory_to_magic(void) {
    if (SDL_GetNumAllocations() > 0) {
        MG_WARN("SDL already holds allocations; leaving its allocator as it is.");
        return;
    }
    SDL_SetMemoryFunctions(sdl_mg_malloc, sdl_mg_calloc, sdl_mg_realloc, sdl_mg_free);
}

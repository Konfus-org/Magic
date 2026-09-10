/**
 * @file allocator.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The three operations every allocation in Magic goes through, and how
 * an app puts its own underneath them.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief Where the bytes come from: the calloc, realloc and free of whoever
 * is supplying them. Not a gem slot, deliberately -- a gem binds partway
 * through mg_load_gems, by which time the log, the registries and the gem
 * loader itself have already allocated, and blocks handed out by one
 * allocator must never go back to another. An app installs its own with
 * mg_use_allocator before the first allocation instead.
 *
 * Every member is called with a size already checked to fit a size_t, and
 * never with a zero size.
 */
typedef struct mg_allocator {
    /**
     * @brief size ZEROED bytes, or NULL if they cannot be had. Zeroed is the
     * contract, not a courtesy: mg_alloc promises it, and the default keeps
     * it in one call by being calloc.
     */
    void* (*alloc)(len size);

    /**
     * @brief Grows or shrinks a block to new_size, realloc's way: resizing
     * NULL allocates, the bytes past the old length are NOT zeroed, and a
     * failure returns NULL with the old block untouched and still the
     * caller's.
     */
    void* (*resize)(void* ptr, len new_size);

    /** @brief Gives a block back. NULL is a no-op, as free(NULL) is. */
    void (*free)(void* ptr);
} mg_allocator;

/**
 * @brief Puts an allocator under mg_alloc, mg_resize and mg_free; NULL puts
 * the libc default back. Borrowed, so it must outlive every block it hands
 * out -- a file-scope or static one.
 *
 * Only before the first allocation. A block belongs to the allocator that
 * handed it out, so swapping once any exist would send them back to the
 * wrong free; this refuses to swap and asserts instead of letting that
 * happen later as a corrupted heap. mg_on_start is already too late, but
 * mg_on_init is not: it fires before the first allocation the engine makes.
 * @param allocator The allocator to install, or NULL for the default.
 */
MG_EXPORT void mg_use_allocator(const mg_allocator* allocator);

/** @brief The allocator in use; never NULL, defaulting to libc's. */
MG_EXPORT const mg_allocator* mg_get_allocator(void);

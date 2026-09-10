/**
 * @file allocator.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The libc allocator, and the post an app's own takes over.
 * Implements magic/memory/allocator.h.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/allocator.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "memory/allocator_internal.h"
#include <stdlib.h>

/* Plain libc and not mg_mem_*: mg_alloc routes through here, so a default
 * that called back into magic/memory would loop. */

/**
 * @brief The default alloc: calloc, which is the zeroing mg_alloc promises
 * done in one call. One item of size bytes rather than the other way round --
 * mg_alloc has already multiplied and checked the product.
 */
static void* mg_libc_alloc(const len size) {
    return calloc(1, (size_t)size);
}

/** @brief The default resize: realloc, whose semantics the slot documents. */
static void* mg_libc_resize(void* ptr, const len new_size) {
    return realloc(ptr, (size_t)new_size);
}

/** @brief The default free. */
static void mg_libc_free(void* ptr) {
    free(ptr);
}

/** @brief What every allocation goes through until an app says otherwise. */
static const mg_allocator g_libc_allocator = {
    .alloc = mg_libc_alloc,
    .resize = mg_libc_resize,
    .free = mg_libc_free,
};

/* The allocator in use. A pointer to the libc one rather than a copy of it,
 * so mg_use_allocator(NULL) is a plain reset. */
static const mg_allocator* g_allocator = &g_libc_allocator;

/* Raised by the first allocation and never lowered. What makes swapping an
 * allocator mid-flight a loud refusal instead of a heap that comes apart much
 * later, somewhere else. Always compiled, tracking or no tracking: this is
 * correctness, not bookkeeping. */
static b8 g_has_allocated = FALSE;

void mg_use_allocator(const mg_allocator* allocator) {
    MG_ASSERT_RETURN_IF(
        !g_has_allocated,
        "an allocator can only be installed before the first allocation: blocks "
        "already handed out would go back to the wrong free. mg_on_init is the "
        "last moment that is still true.");

    /* A half-filled allocator would fail at the first call it is missing, far
     * from here; refuse it while there is still a name to put on the error. */
    MG_ASSERT_RETURN_IF(
        allocator == NULL
            || (allocator->alloc != NULL && allocator->resize != NULL && allocator->free != NULL),
        "an allocator needs all three of alloc, resize and free");

    g_allocator = allocator != NULL ? allocator : &g_libc_allocator;
}

const mg_allocator* mg_get_allocator(void) {
    return g_allocator;
}

void mg_allocator_mark_used(void) {
    g_has_allocated = TRUE;
}

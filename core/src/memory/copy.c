/**
 * @file copy.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Raw byte moves. Implements magic/memory/copy.h. Straight libc and
 * not gem slots: the engine needs memory before a gem is ever loaded.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/copy.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include <string.h>

/** @brief Bytes exchanged per pass of the swap loop. */
#define MG_SWAP_CHUNK 256

/** @brief Whether size survives the trip from len into size_t. */
static b8 mg_fits_size(const len size) {
    const size_t narrow = (size_t)size;
    return (len)narrow == size;
}

void mg_mem_copy(void* destination, const void* source, const len size) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(source != NULL, "source must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    /* A copy of nothing is normal, and the one-past-the-end pointer a caller
     * naturally passes at count 0 need not be valid for memcpy. */
    if (size == 0) {
        return;
    }

    memcpy(destination, source, (size_t)size);
}

void mg_mem_fill(void* destination, const len size, const byte value) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    memset(destination, (int)value, (size_t)size);
}

void mg_mem_zero(void* destination, const len size) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    memset(destination, 0, (size_t)size);
}

void mg_mem_move(void* destination, const void* source, const len size) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(source != NULL, "source must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    /* memmove and not memcpy: memcpy is undefined where the two overlap,
     * and overlapping is the whole reason to call this. */
    memmove(destination, source, (size_t)size);
}

void mg_mem_swap(void* from, void* to, const len size) {
    MG_ASSERT_RETURN_IF(from != NULL, "from must not be NULL");
    MG_ASSERT_RETURN_IF(to != NULL, "to must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    /* Copying a region onto itself is undefined, and a swap with
     * itself is a no-op anyway. */
    if (size == 0 || from == to) {
        return;
    }

    /* A fixed scratch walked in passes: an allocation would add a failure mode
     * this void function cannot report. */
    byte* left = from;
    byte* right = to;

    /* Widened once: the define is an int and every size here is a len. */
    const len chunk_size = MG_SWAP_CHUNK;

    for (len moved = 0; moved < size; moved += chunk_size) {
        byte scratch[MG_SWAP_CHUNK];
        const len remaining = size - moved;
        const len chunk = remaining < chunk_size ? remaining : chunk_size;

        memcpy(scratch, left + moved, (size_t)chunk);
        memcpy(left + moved, right + moved, (size_t)chunk);
        memcpy(right + moved, scratch, (size_t)chunk);
    }
}

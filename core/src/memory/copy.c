/**
 * @file copy.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Raw byte moves. Implements magic/memory/copy.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/copy.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/memory/slots.h"
#include <stdint.h>

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

    /* A box carries a plain void*, and -Wcast-qual rejects casting the const
     * off source directly; the trip through uintptr_t is the accepted
     * spelling. The slot only reads source. */
    const mg_box dst = {.data = destination, .size = size};
    const mg_box src = {.data = (void*)(uintptr_t)source, .size = size};
    gs_mem_copy(dst, src);
}

void mg_mem_fill(void* destination, const len size, const byte value) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    const mg_box b = {.data = destination, .size = size};
    gs_mem_fill(b, value);
}

void mg_mem_zero(void* destination, const len size) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    /* The zero slot and not mg_mem_fill(destination, size, 0): a gem may
     * provide a dedicated zero without providing fill, and going through fill
     * would skip it. */
    const mg_box b = {.data = destination, .size = size};
    gs_mem_zero(b);
}

void mg_mem_move(void* destination, const void* source, const len size) {
    MG_ASSERT_RETURN_IF(destination != NULL, "destination must not be NULL");
    MG_ASSERT_RETURN_IF(source != NULL, "source must not be NULL");
    MG_ASSERT_RETURN_IF(mg_fits_size(size), "size does not fit a size_t");

    if (size == 0) {
        return;
    }

    /* The move slot and not the copy one: copy is memcpy, which is undefined
     * where the two overlap, and overlapping is the whole reason to call
     * this. */
    const mg_box dst = {.data = destination, .size = size};
    const mg_box src = {.data = (void*)(uintptr_t)source, .size = size};
    gs_mem_move(dst, src);
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

        const mg_box scratch_box = {.data = scratch, .size = chunk};
        const mg_box left_box = {.data = left + moved, .size = chunk};
        const mg_box right_box = {.data = right + moved, .size = chunk};

        gs_mem_copy(scratch_box, left_box);
        gs_mem_copy(left_box, right_box);
        gs_mem_copy(right_box, scratch_box);
    }
}

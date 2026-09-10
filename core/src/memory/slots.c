/**
 * @file slots.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The byte moves every memory slot falls back to. Implements
 * magic/memory/slots.h.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/slots.h"
#include <string.h>

/* Plain libc and not mg_mem_*: mg_mem_copy/fill/zero/move route through these
 * slots, so a default that called back into magic/memory would loop. */

/**
 * @brief Whether size survives the trip from u64 into size_t. A round trip
 * rather than a SIZE_MAX compare, which is tautological on a 64-bit target.
 */
static b8 mg_default_mem_fits_(const u64 size) {
    const size_t narrow = (size_t)size;
    return (u64)narrow == size;
}

/**
 * @brief gs_mem_zero when no gem provides one. A box with no data, no
 * size, or a size that does not fit a size_t comes back untouched.
 */
static mg_box mg_default_mem_zero_(const mg_box b) {
    if (b.data != NULL && b.size > 0 && mg_default_mem_fits_(b.size)) {
        memset(b.data, 0, (size_t)b.size);
    }
    return b;
}

/**
 * @brief gs_mem_fill when no gem provides one. The same guards as
 * mg_default_mem_zero_.
 */
static mg_box mg_default_mem_fill_(const mg_box b, const u8 value) {
    if (b.data != NULL && b.size > 0 && mg_default_mem_fits_(b.size)) {
        memset(b.data, (int)value, (size_t)b.size);
    }
    return b;
}

/**
 * @brief gs_mem_copy when no gem provides one.
 * Copies the smaller of the two sizes, so neither box is overrun.
 */
static mg_box mg_default_mem_copy_(const mg_box dst, const mg_box src) {
    const u64 size = src.size < dst.size ? src.size : dst.size;
    if (dst.data != NULL && src.data != NULL && size > 0 && mg_default_mem_fits_(size)) {
        memcpy(dst.data, src.data, (size_t)size);
    }
    return dst;
}

/**
 * @brief gs_mem_move when no gem provides one: memmove, which is the whole
 * difference from copy. The same smaller-of-the-two guard.
 */
static mg_box mg_default_mem_move_(const mg_box dst, const mg_box src) {
    const u64 size = src.size < dst.size ? src.size : dst.size;
    if (dst.data != NULL && src.data != NULL && size > 0 && mg_default_mem_fits_(size)) {
        memmove(dst.data, src.data, (size_t)size);
    }
    return dst;
}

MG_DEFINE_GEM_SLOT(mg_box, gs_mem_zero, mg_default_mem_zero_, mg_box b);
MG_DEFINE_GEM_SLOT(mg_box, gs_mem_fill, mg_default_mem_fill_, mg_box b, u8 value);
MG_DEFINE_GEM_SLOT(mg_box, gs_mem_copy, mg_default_mem_copy_, mg_box dst, mg_box src);
MG_DEFINE_GEM_SLOT(mg_box, gs_mem_move, mg_default_mem_move_, mg_box dst, mg_box src);

void mg_memory_register_gem_slots(void) {
    MG_REGISTER_GEM_SLOT(gs_mem_zero);
    MG_REGISTER_GEM_SLOT(gs_mem_fill);
    MG_REGISTER_GEM_SLOT(gs_mem_copy);
    MG_REGISTER_GEM_SLOT(gs_mem_move);
}

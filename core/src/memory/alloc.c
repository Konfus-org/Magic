/**
 * @file alloc.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The one heap everything Magic owns is allocated from. Implements
 * magic/memory/alloc.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/alloc.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/memory/allocator.h"
#include "memory/allocator_internal.h"
#include "memory/tracked.h"
#include <stddef.h>

/* The gate the ledger and its header ride on. Always defined, never left to
 * the preprocessor's implicit 0, because -Wundef is an error here. */
#ifndef MG_TRACK_MEMORY
    #define MG_TRACK_MEMORY 0
#endif

#if MG_TRACK_MEMORY

/**
 * @brief The bookkeeping in front of every allocation: the payload's byte
 * count, which free and resize read back so the stats can count what the
 * public API never passes them. The union pads the header to full alignment,
 * so the payload right after it is as aligned as the allocator's own return
 * was.
 *
 * Here only while tracking is: it exists to answer "how many bytes is this
 * block" for a free that was told nothing, and with no books to keep there is
 * nothing to answer. A build without tracking hands the allocator's own
 * pointer straight back.
 */
typedef union mg_alloc_header {
    len size; /**< Payload bytes, the header not included. */

    /* Never read. The widest fundamental types, so the payload right past the
     * header is aligned for any of them. C11 says this in one word --
     * max_align_t -- which MSVC declares only in C++, so the types are named
     * instead. Naming them also tracks each ABI's own answer: 16 bytes where
     * long double needs it, 8 where it does not. */
    long long align_integer;
    long double align_float;
    void* align_pointer;
    void (*align_function)(void);
} mg_alloc_header;

    /** @brief What the payload sits behind; zero when nothing is tracked. */
    #define MG_ALLOC_OVERHEAD sizeof(mg_alloc_header)

#else

    #define MG_ALLOC_OVERHEAD 0u

#endif // MG_TRACK_MEMORY

/**
 * @brief Whether count items of size bytes can be asked for at all, the
 * hidden header included. A cast-and-back round trip rather than a compare
 * against SIZE_MAX, which on a 64-bit target is tautological and an error
 * under -Werror.
 */
static b8 mg_fits_allocation(const len count, const len size) {
    const size_t narrow_count = (size_t)count;
    const size_t narrow_size = (size_t)size;

    if ((len)narrow_count != count || (len)narrow_size != size) {
        return FALSE;
    }

    /* size is never zero at either call site -- both reject it first -- but the
     * division has to be guarded where it is written, not where it is called. */
    if (narrow_size == 0) {
        return FALSE;
    }

    return narrow_count <= (SIZE_MAX - MG_ALLOC_OVERHEAD) / narrow_size;
}

void* mg_alloc(const len count, const len size, enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_VAL_IF(count > 0, NULL, "count must not be zero");
    MG_ASSERT_RETURN_VAL_IF(size > 0, NULL, "size must not be zero");

    if (!mg_fits_allocation(count, size)) {
        MG_ERROR(
            "Refusing an allocation of %llu x %llu bytes: the size does not fit a size_t.",
            (unsigned long long)count,
            (unsigned long long)size);
        return NULL;
    }

    const len bytes = count * size;

    /* Through the allocator, so an app can put its own underneath the engine.
     * The zeroing alloc.h promises is the allocator's own contract, kept in
     * one call by the calloc default. */
    void* block = mg_get_allocator()->alloc(MG_ALLOC_OVERHEAD + bytes);
    if (block == NULL) {
        MG_ERROR(
            "Out of memory: %llu x %llu bytes.",
            (unsigned long long)count,
            (unsigned long long)size);
        return NULL;
    }

    /* Only now: a refused allocation is not a block, and must not be what
     * closes the door on mg_use_allocator. */
    mg_allocator_mark_used();

#if MG_TRACK_MEMORY
    /* Tracked only once the allocation exists; a refusal is not usage. */
    mg_track_alloc(bytes, tag);

    // The caller's pointer is the first byte past the header.
    mg_alloc_header* header = block;
    header->size = bytes;
    return header + 1;
#else
    MG_UNUSED(tag);
    return block;
#endif
}

void* mg_resize(void* ptr, const len count, const len item_size, enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_VAL_IF(count > 0, NULL, "count must not be zero");
    MG_ASSERT_RETURN_VAL_IF(item_size > 0, NULL, "item_size must not be zero");

    if (!mg_fits_allocation(count, item_size)) {
        MG_ERROR(
            "Refusing a resize to %llu x %llu bytes: the size does not fit a size_t.",
            (unsigned long long)count,
            (unsigned long long)item_size);
        return NULL;
    }

    const len new_bytes = count * item_size;

#if MG_TRACK_MEMORY
    /* A NULL ptr has no header in front of it, and its old length is 0 -- the
     * realloc semantics the allocator keeps make that a plain allocation. */
    mg_alloc_header* header = ptr != NULL ? (mg_alloc_header*)ptr - 1 : NULL;
    const len old_bytes = header != NULL ? header->size : 0;
    void* old_block = header;
#else
    void* old_block = ptr;
#endif

    /* Through the allocator's resize, which keeps realloc semantics: resizing
     * NULL allocates, and on failure the old block is untouched and still the
     * caller's to free, so the result must not go straight back over ptr. */
    void* resized = mg_get_allocator()->resize(old_block, MG_ALLOC_OVERHEAD + new_bytes);
    if (resized == NULL) {
        MG_ERROR(
            "Out of memory resizing to %llu x %llu bytes. The old allocation is untouched.",
            (unsigned long long)count,
            (unsigned long long)item_size);
        return NULL;
    }

    mg_allocator_mark_used();

#if MG_TRACK_MEMORY
    mg_track_resize(old_bytes, new_bytes, tag);

    /* Bytes past the old length are not zeroed: the allocator keeps realloc
     * semantics, and zeroing here would need a length it moved without. */
    mg_alloc_header* moved = resized;
    moved->size = new_bytes;
    return moved + 1;
#else
    MG_UNUSED(tag);
    return resized;
#endif
}

void mg_free(void* ptr, enum mg_memory_tag tag) {
    /* NULL stays a no-op, as free(NULL) is -- and it has no header to read. */
    if (ptr == NULL) {
        return;
    }

#if MG_TRACK_MEMORY
    /* What goes back to the allocator is what came from it: the header's
     * address, not the caller's. */
    mg_alloc_header* header = (mg_alloc_header*)ptr - 1;
    mg_track_free(header->size, tag);
    mg_get_allocator()->free(header);
#else
    MG_UNUSED(tag);
    mg_get_allocator()->free(ptr);
#endif
}

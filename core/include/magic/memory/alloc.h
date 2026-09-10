/**
 * @file alloc.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The one heap everything Magic owns is allocated from.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/memory/stats.h"
#include "magic/primitives.h"

/**
 * @brief The largest allocation a size_t can express, given as a len.
 * Sizes are carried in len (u64) but spent in size_t; on a 32-bit target
 * size_t is the smaller ceiling, and the one that binds.
 */
#define MG_MAX_ALLOCATION SIZE_MAX

/** @brief One zeroed instance of type, as a type*. */
#define mg_alloc_one(type, tag) ((type*)mg_alloc(1, sizeof(type), tag))

/** @brief A zeroed buffer of count instances of type. */
#define mg_alloc_buffer(type, count, tag) ((type*)mg_alloc((count), sizeof(type), tag))

/**
 * @brief Resizes ptr to new_cap items of item_size bytes. New memory is NOT
 * zeroed; a caller who needs that zeroes past its own old count.
 */
#define mg_resize_buffer(ptr, new_cap, item_size, tag)                                          \
    mg_resize(ptr, (new_cap), (item_size), tag)

/** @brief A zeroed run of the given number of bytes, as a void*. */
#define mg_alloc_bytes(bytes, tag) mg_alloc(1, (bytes), tag)

/** @brief Releases whatever one item owns. Does not free the item itself. */
typedef void (*mg_destructor)(void* item);

/**
 * @brief count zeroed items of size bytes each.
 * Rejects a zero count or size, and a product too large for a size_t.
 * @return The allocation, or NULL on failure.
 */
MG_EXPORT void* mg_alloc(len count, len size, enum mg_memory_tag tag);

/**
 * @brief Resizes an allocation to count items of item_size bytes each.
 * Bytes past the old length are not zeroed, unlike mg_alloc's.
 * @return The possibly-moved block, or NULL on failure with ptr still valid
 * and still the caller's to free.
 */
MG_EXPORT void* mg_resize(void* ptr, len count, len item_size, enum mg_memory_tag tag);

/** @brief Releases an allocation from mg_alloc or mg_resize. NULL is a no-op. */
MG_EXPORT void mg_free(void* ptr, enum mg_memory_tag tag);

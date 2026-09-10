/**
 * @file copy.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Raw byte moves: copy, fill, zero, move and swap.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief A copy of what thing points at, as a value: plain C assignment,
 * which copies structs whole, spelled so the call site says so. The type
 * comes from thing itself, so there is no size to state or get wrong, and a
 * wrong destination type is a compile error. A shallow copy, like every byte
 * copy: pointers inside come along, what they point at does not.
 * @param thing The address of what to copy.
 * @code
 * mg_event evt_copy = mg_copy(evt);
 * @endcode
 */
#define mg_copy(thing) (*(thing))

/** @brief Copies size bytes into destination. The two must not overlap. */
MG_EXPORT void mg_mem_copy(void* destination, const void* source, len size);

/** @brief Writes value over size bytes at destination. */
MG_EXPORT void mg_mem_fill(void* destination, len size, byte value);

/** @brief Writes zeroes over size bytes at destination. */
MG_EXPORT void mg_mem_zero(void* destination, len size);

/**
 * @brief Copies size bytes into destination, even when the two overlap.
 * The same argument order as mg_mem_copy: destination = source.
 */
MG_EXPORT void mg_mem_move(void* destination, const void* source, len size);

/** @brief Exchanges size bytes between from and to. */
MG_EXPORT void mg_mem_swap(void* from, void* to, len size);

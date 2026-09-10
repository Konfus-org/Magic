/**
 * @file tracked.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The private counting half of magic/memory/stats.h: alloc.c reports
 * every allocation, resize and free here, and the public getters read the totals.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/memory/stats.h"

/**
 * @brief Counts size bytes as allocated under tag.
 * @param size Bytes handed out.
 * @param tag Where the bytes are accounted.
 */
void mg_track_alloc(len size, enum mg_memory_tag tag);

/**
 * @brief Counts size bytes as deallocated under tag.
 * @param size Bytes taken back.
 * @param tag Where the bytes are accounted.
 */
void mg_track_free(len size, enum mg_memory_tag tag);

/**
 * @brief Counts a resize: old_size bytes deallocated and new_size allocated,
 * so live usage stays allocated minus deallocated with no special case.
 * @param old_size Bytes the block held before.
 * @param new_size Bytes the block holds now.
 * @param tag Where both figures are accounted.
 */
void mg_track_resize(len old_size, len new_size, enum mg_memory_tag tag);

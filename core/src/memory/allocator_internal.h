/**
 * @file allocator_internal.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The private half of magic/memory/allocator.h: what alloc.c tells
 * allocator.c, and nobody outside magic/memory says at all.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/memory/allocator.h"

/**
 * @brief Notes that a block has been handed out, which is what closes the
 * door on mg_use_allocator. Called by mg_alloc, once it has an allocation to
 * report; a refused one is not a block.
 */
void mg_allocator_mark_used(void);

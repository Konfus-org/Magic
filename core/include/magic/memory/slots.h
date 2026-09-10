/**
 * @file slots.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The byte moves a gem may put its own implementation under: zero,
 * fill, copy and move. Allocation is NOT here -- it is an app's to install
 * through magic/memory/allocator.h, for the reasons that header gives.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/gems/slots.h"
#include "magic/memory/box.h"
#include "magic/primitives.h"

/* Every slot holds a default until a gem fills it, so a call site never
 * checks for NULL: with no gem, the byte moves are libc's. The defaults live
 * in core/src/memory/slots.c; a new slot adds its declare here, and its
 * default, define and register line there. These are worth handing to a gem
 * where allocation is not: SDL's SDL_memset and SDL_memcpy are vectorised,
 * and swapping them in is a real win where swapping malloc for malloc was
 * not. */

/** @brief Registers every memory slot with the engine's gem registry. */
MG_EXPORT void mg_memory_register_gem_slots(void);

/** @brief Sets all bytes in a memory block to zero. */
MG_DECLARE_GEM_SLOT(mg_box, gs_mem_zero, mg_box b);

/** @brief Fills memory with a given value. */
MG_DECLARE_GEM_SLOT(mg_box, gs_mem_fill, mg_box b, u8 value);

/** @brief Copies memory from one location to another. The two must not overlap. */
MG_DECLARE_GEM_SLOT(mg_box, gs_mem_copy, mg_box dst, mg_box src);

/**
 * @brief Copies memory from one location to another, overlap allowed. Its own
 * slot and not copy with a caveat: the two answer to different libc functions,
 * and a gem that fills copy with a vectorised memcpy would otherwise silently
 * take the overlapping case with it.
 */
MG_DECLARE_GEM_SLOT(mg_box, gs_mem_move, mg_box dst, mg_box src);

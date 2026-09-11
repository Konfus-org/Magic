/**
 * @file sdl_base.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief What the SDL base gem's pieces trade with each other: one
 * declaration per implementation, grouped per concern. Each concern lives in
 * the .c named after it -- sdl_memory.c, sdl_logging.c -- and sdl_base_gem.c
 * binds them into the one registration. Internal to the gem; nothing here is
 * exported, and neither SDL_platform nor SDL_gpu includes it -- what they get
 * from this gem is an SDL already routed through the engine, not a symbol.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/primitives.h"
#include <SDL3/SDL.h>

/* ========================================================================== */
/* Memory (sdl_memory.c)                                                      */
/* ========================================================================== */

/**
 * @brief Points SDL's allocator at the engine's, so SDL's internal usage
 * shows in the tag ledger. Only safe while SDL holds no allocations, so a
 * hot reload that left any live skips the redirect and keeps SDL on whatever
 * allocator those blocks came from.
 */
void sdl_route_memory_to_magic(void);

/* ========================================================================== */
/* Logging (sdl_logging.c)                                                    */
/* ========================================================================== */

/**
 * @brief Points SDL's log at the engine's, so everything SDL says comes out
 * of MG_TRACE through MG_FATAL alongside the engine's own lines, under
 * whatever loggers the app registered. SDL is opened all the way up as it
 * goes: which severities are heard is the engine's call, made at compile time
 * in the MG_* macros, not SDL's to filter first.
 */
void sdl_route_logging_to_magic(void);

/**
 * @brief Puts SDL's own log writer back, before this image goes away. The
 * callback sdl_route_logging_to_magic installed lives in this gem, so leaving
 * it in place past an unload leaves SDL calling into an unmapped page.
 */
void sdl_restore_logging(void);

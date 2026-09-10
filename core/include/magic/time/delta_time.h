/**
 * @file delta_time.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The interval since the last poll, and the reading to carry into the next.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

#define MG_FIXED_DT (ts)(1.0 / 60.0) // 0.016667 60 FPS

/** @brief Calculate the interval since the last poll, and the reading to carry into the next. */
MG_EXPORT ts mg_calculate_dt(ts* last_poll);

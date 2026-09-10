/**
 * @file timestamp.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief A monotonic clock, read in milliseconds, nanoseconds or seconds.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

/*
 * Monotonic, so an NTP correction or a daylight-saving change cannot land as
 * one catastrophic frame delta. The epoch is unspecified and differs per
 * platform: only differences between readings mean anything.
 *
 * Every reading is a t64 so an interval keeps its fraction -- a 144 Hz frame is
 * 6.94 ms, which at integer resolution would alternate and drift.
 */

/**
 * @brief Milliseconds since an unspecified epoch. The counter the other two
 * are derived from.
 */
MG_EXPORT ts mg_get_timestamp_ms(void);

/** @brief The same reading in nanoseconds. Scaled, not finer. */
MG_EXPORT ts mg_get_timestamp_ns(void);

/** @brief The same reading in seconds. Scaled, not finer. */
MG_EXPORT ts mg_get_timestamp_s(void);

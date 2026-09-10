/**
 * @file frame.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief One frame's worth of timing state, advanced a poll at a time.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief The timing a frame carries between polls.
 * Zero-initialize it: a zero last_poll means "never polled", so the first
 * frame gets a dt of 0 rather than the entire uptime.
 */
typedef struct mg_frame {
    ind index;
    ts dt;
    ts accumulator;
    ts last_poll;
} mg_frame;

/**
 * @brief Advances frame by one poll: reads the clock, records the interval
 * since the last poll, and counts the frame.
 */
MG_EXPORT void mg_next_frame(mg_frame* frame);

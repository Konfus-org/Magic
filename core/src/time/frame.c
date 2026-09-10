/**
 * @file frame.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief GENERATED-STUBS -- every function here traps when called.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/time/frame.h"
#include "magic/time/delta_time.h"

void mg_next_frame(mg_frame* frame) {
    frame->dt = mg_calculate_dt(&frame->last_poll);
    frame->accumulator += frame->dt;
}

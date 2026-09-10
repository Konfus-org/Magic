/**
 * @file delta_time.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief GENERATED-STUBS -- every function here traps when called.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/time/delta_time.h"
#include "magic/time/timestamp.h"

ts mg_calculate_dt(ts* last_poll) {
    const ts time = mg_get_timestamp_ms();
    const ts delta = time - *last_poll;
    *last_poll = time;
    return delta;
}

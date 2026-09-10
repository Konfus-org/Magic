/**
 * @file timestamp.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief GENERATED-STUBS -- every function here traps when called.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/time/timestamp.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/platform/slots.h"

ts mg_get_timestamp_ms(void) {
    return gs_platform_get_timestamp();
}

ts mg_get_timestamp_ns(void) {
    return mg_get_timestamp_ms() * 1000;
}

ts mg_get_timestamp_s(void) {
    return mg_get_timestamp_ms() / 1000;
}

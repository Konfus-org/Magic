/**
 * @file datetime.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/time/datetime.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/platform/slots.h"

mg_datetime mg_get_datetime(void) {
    return gs_platform_get_datetime();
}

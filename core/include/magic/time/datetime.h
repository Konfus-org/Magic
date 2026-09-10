/**
 * @file datetime.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/primitives.h"

typedef struct mg_datetime {
    ts milliseconds;
    ts seconds;
    ts day;
    ts month;
    ts year;
} mg_datetime;

mg_datetime mg_get_datetime(void);

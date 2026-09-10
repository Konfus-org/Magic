/**
 * @file id.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The identifier type, and the two ways to compare one.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/debug/asserts.h"
#include "magic/primitives.h"

#define MG_NIL_ID ((mg_id) {.high = 0, .low = 0})

/**
 * @brief An identifier, usually randomly generated like a GUID or UUID.
 * Two 64-bit halves, because C has no portable 128-bit integer. Compare with
 * mg_id_eq; a struct has no == in C.
 */
typedef struct mg_id {
    u64 high;
    u64 low;
} mg_id;

STATIC_ASSERT(sizeof(mg_id) == 16, "Expected mg_id to be 16 bytes.");

/** @brief A fresh random id, or MG_NIL_ID if the platform's entropy source failed. */
MG_EXPORT mg_id mg_construct_id(void);

/** @brief Is the id not nil. */
b8 mg_id_valid(mg_id id);

/** @brief Is id a equal by value to id b. */
b8 mg_id_eq(mg_id a, mg_id b);

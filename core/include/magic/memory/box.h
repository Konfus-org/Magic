/**
 * @file box.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Untyped data carried with its size.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/primitives.h"

/** @brief Boxes a pointer with the size of what it points at. */
#define mg_boxup(ptr) (LITERAL(mg_box) {(ptr), sizeof(*(ptr))})

/** @brief The box carrying nothing: NULL data, zero size. */
#define MG_NIL_BOX (LITERAL(mg_box) {NULL, 0})

/**
 * @brief Reads a box back as type. A box carries a size, not a type; it is
 * on the caller to unbox what was boxed.
 */
#define mg_unbox(type, boxed) ((type*)(boxed).data)

/** @brief Any type of data with its size. Build with MG_BOX, read with MG_UNBOX. */
typedef struct mg_box {
    void* data;
    u64 size;
} mg_box;

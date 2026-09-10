/**
 * @file view.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The generic borrowed window every collection is read through.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/primitives.h"

/**
 * @brief A borrowed, read-only window on count items of item_size bytes: the
 * one shape every collection views as, so anything that reads a view reads
 * them all. A view owns nothing and is never freed; only the viewed items'
 * owner outliving the view keeps one valid. Each collection views under its
 * own name: mg_buffer_view here, the minted mg_<name>_array_view and
 * mg_<name>_list_view per typed collection, and mg_str_view in string.h.
 */
typedef struct mg_view {
    const void* items; /**< The items viewed; borrowed, never freed. */
    len item_size; /**< Bytes per curr, and the stride between them. */
    len count; /**< How many items the view spans. */
} mg_view;

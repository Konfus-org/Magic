/**
 * @file iter.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The generic iterator: a cursor over a view, stepped in either direction.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/view.h"
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief A walk over a view, one item per step: works on anything a view can
 * be made of, in the manner of mg_get_cmd's scan. Plain stack data borrowing
 * from the view, so there is nothing to free after. The cursor stands between
 * items: next hands back the item ahead of it and steps past it, previous the
 * item behind it and steps back over it, so the two undo each other.
 * @code
 * mg_iter it = mg_iter_construct(mg_buffer_view(&scores));
 * const u32* score;
 * while ((score = mg_iter_next(&it)) != NULL) {
 *     ...
 * }
 * @endcode
 */
typedef struct mg_iter {
    const void* curr; /**< What the last step landed on. */
    mg_view view; /**< What is walked. */
    len index; /**< curr's index in the view, while curr is not NULL. */
    len cursor; /**< Where the walk stands. */
} mg_iter;

/**
 * @brief An iterator standing before a view's first item, where mg_iter_next
 * begins a front-to-back walk; every walk starts from a fresh one. Allocates
 * nothing.
 */
MG_EXPORT mg_iter mg_iter_construct(mg_view view);

/**
 * @brief An iterator standing past a view's last item, where
 * mg_iter_previous begins a back-to-front walk.
 */
MG_EXPORT mg_iter mg_iter_construct_reverse(mg_view view);

/**
 * @brief Steps the iterator forward over the next item, into it->item and
 * it->index.
 * @return The item stepped over, or NULL when the view is exhausted, which
 * ends the walk with it->item back at NULL.
 */
MG_EXPORT const void* mg_iter_next(mg_iter* it);

/**
 * @brief Steps the iterator backward over the previous item, into it->item
 * and it->index.
 * @return The item stepped over, or NULL when the walk is back before the
 * first item, which ends it with it->item back at NULL.
 */
MG_EXPORT const void* mg_iter_previous(mg_iter* it);

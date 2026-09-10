/**
 * @file iter.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The generic iterator. Implements magic/collections/iter.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/collections/iter.h"
#include "magic/debug/logging.h"

/* logging.h is here for MG_FATAL, which the assert macros expand to;
 * asserts.h cannot include it itself without closing an include loop. */

mg_iter mg_iter_construct(const mg_view view) {
    const mg_iter it = {.curr = NULL, .view = view, .index = 0, .cursor = 0};
    return it;
}

mg_iter mg_iter_construct_reverse(const mg_view view) {
    const mg_iter it = {.curr = NULL, .view = view, .index = 0, .cursor = view.count};
    return it;
}

const void* mg_iter_next(mg_iter* it) {
    MG_ASSERT_RETURN_VAL_IF(it != NULL, NULL, "it must not be NULL");

    if (it->cursor >= it->view.count) {
        it->curr = NULL;
        return NULL;
    }

    it->index = it->cursor;
    it->curr = (const byte*)it->view.items + (it->index * it->view.item_size);
    it->cursor++;
    return it->curr;
}

const void* mg_iter_previous(mg_iter* it) {
    MG_ASSERT_RETURN_VAL_IF(it != NULL, NULL, "it must not be NULL");

    if (it->cursor == 0) {
        it->curr = NULL;
        return NULL;
    }

    it->cursor--;
    it->index = it->cursor;
    it->curr = (const byte*)it->view.items + (it->index * it->view.item_size);
    return it->curr;
}

/**
 * @file string.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The owned, immutable string, and the character view every string
 * operation reads through.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/collections/view.h"
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief A view of characters: the generic view under the name every string
 * operation takes and returns, so a signature says "characters" instead of
 * "items of some size". The same borrowed, read-only window underneath --
 * anything that reads a view reads one of these. Construct one from an owned
 * string with mg_view_str, or from a C string -- a literal included -- with
 * mg_view_cstr.
 */
typedef mg_view mg_str_view;

/**
 * @brief count characters that never change, with a NUL kept past the last.
 * items and count, the view's own shape, so a string reads through everything
 * a view reads through. Every operation that "changes" a string builds a new
 * one and leaves its inputs alone. Each string owns its characters and is
 * freed exactly once; copies made by = share them, so free only one of the
 * two. A view of one is NOT terminated by promise: the NUL past a view of the
 * middle belongs to the whole string, which is why only mg_str_to_cstr, on
 * the owned string, bridges to C string takers.
 */
typedef struct mg_str {
    const char* items; /**< The characters, or NULL for the out-of-memory string. */
    len count; /**< How many characters, not counting the terminator. */
} mg_str;

/* The list of owned strings, minted here where mg_str lives. The list frees
 * only its own slots: each string in it is still freed by whoever owns it,
 * one mg_str_free per string, before the list itself goes. */
MG_DEFINE_LIST(mg_str, NULL);

/**
 * @brief A view of everything str holds. Borrows from str, so it is valid
 * only as long as str is not freed.
 */
MG_EXPORT mg_str_view mg_view_str(mg_str str);

/**
 * @brief A view of everything before cstr's terminator, walked once to
 * count it; NULL views as empty. Borrows from cstr for as long as it lives.
 * A literal is a cstr like any other, so this views one too.
 */
MG_EXPORT mg_str_view mg_view_cstr(const char* cstr);

/**
 * @brief A new string copying view's characters: the one way a view's
 * borrowed characters become owned ones. Free with mg_str_free.
 * @return Valid and terminated; a NULL items only on out of memory.
 */
MG_EXPORT mg_str mg_str_new(mg_str_view view);

/**
 * @brief Releases the characters. Leaves the empty out-of-memory-shaped
 * string, so freeing twice is a no-op. Any view of the string dangles the
 * moment this returns.
 */
MG_EXPORT void mg_str_free(mg_str* str);

/**
 * @brief How many characters cstr holds before its terminator.
 * @return The count, or 0 for NULL.
 */
MG_EXPORT len mg_cstr_length(const char* cstr);

/**
 * @brief Whether str holds no characters: no items, or none of them. The one
 * question both the empty string and the out-of-memory string answer TRUE to.
 */
MG_EXPORT b8 mg_str_empty(mg_str str);

/**
 * @brief mg_str_empty for a view: whether it spans no characters, whether
 * because there were none or because it was narrowed down to none.
 */
MG_EXPORT b8 mg_str_view_empty(mg_str_view view);

/**
 * @brief str's characters, borrowed: terminated, still owned by str. The
 * bridge to anything wanting a C string, which a view never is: only an
 * owned string guarantees the terminator.
 * @return The characters, or NULL for the out-of-memory string.
 */
MG_EXPORT const char* mg_str_to_cstr(mg_str str);

/**
 * @brief view's characters as a C string, for callers that want one. A view
 * carries no promised terminator -- only mg_str_to_cstr, on an owned string,
 * truly guarantees one -- so this is safe only when view was built with
 * mg_view_cstr or mg_view_str over a WHOLE string and never narrowed since;
 * the caller's guarantee, not this function's.
 */
MG_EXPORT const char* mg_str_view_to_cstr(mg_str_view view);

/**
 * @brief A new string: str's characters, then append's. Borrows both, so no
 * string need be made just to concatenate. Free with mg_str_free.
 */
MG_EXPORT mg_str mg_str_append(mg_str_view str, mg_str_view append);

/**
 * @brief The view without str's leading and trailing whitespace. A narrower
 * borrow of the same characters: nothing allocates, nothing to free.
 */
MG_EXPORT mg_str_view mg_str_trim(mg_str_view str);

/** @brief The view without str's leading whitespace. */
MG_EXPORT mg_str_view mg_str_trim_left(mg_str_view str);

/** @brief The view without str's trailing whitespace. */
MG_EXPORT mg_str_view mg_str_trim_right(mg_str_view str);

/** @brief Whether a and b hold the same characters. */
MG_EXPORT b8 mg_str_eq(mg_str_view a, mg_str_view b);

/** @brief Whether str contains substr. */
MG_EXPORT b8 mg_str_contains(mg_str_view str, mg_str_view substr);

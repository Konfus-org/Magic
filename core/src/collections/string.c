/**
 * @file string.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The immutable string. Implements magic/collections/string.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/collections/string.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/memory/alloc.h"
#include "magic/memory/copy.h"
#include <stdint.h>
#include <string.h>

/**
 * @brief Whether c is whitespace by the C locale's answer, not the platform's.
 * Hand written because isspace is locale dependent and needs an unsigned-char cast.
 */
static b8 mg_is_space(const char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
}

/**
 * @brief view's items as the characters every operation here reads them as.
 * A generic view carries void*, which [] does not compile on.
 */
static const char* mg_chars(const mg_str_view view) {
    return view.items;
}

/**
 * @brief count + 1 zeroed chars, so the terminator is already in place.
 * NULL on out of memory.
 */
static char* mg_str_alloc(const len count) {
    return mg_alloc_buffer(char, count + 1, MG_MEMORY_TAG_STRING);
}

mg_str_view mg_view_str(const mg_str str) {
    const mg_str_view view = {.items = str.items, .item_size = sizeof(char), .count = str.count};
    return view;
}

mg_str_view mg_view_cstr(const char* cstr) {
    const mg_str_view view = {
        .items = cstr,
        .item_size = sizeof(char),
        .count = mg_cstr_length(cstr)};
    return view;
}

mg_str mg_str_new(const mg_str_view view) {
    mg_str out = {.items = NULL, .count = 0};

    char* items = mg_str_alloc(view.count);
    if (items == NULL) {
        return out;
    }

    /* Guarded on the count and not left to mem_copy's own zero-size return,
     * because at count 0 the source may be NULL -- an empty view, or one of
     * the out-of-memory string -- and mem_copy asserts its pointers before it
     * looks at its size. */
    if (view.count > 0) {
        mg_mem_copy(items, view.items, view.count);
    }

    out.items = items;
    out.count = view.count;
    return out;
}

void mg_str_free(mg_str* str) {
    MG_ASSERT_RETURN_IF(str != NULL, "str must not be NULL");

    /* The characters are const to every reader, but they are still this
     * module's allocation to release -- the const was put on by mg_str_new
     * and comes off here, at the other end of the same file. Routed through
     * uintptr_t because a pointer cast that drops a qualifier is exactly what
     * -Wcast-qual rejects, and an integer round-trip is the standard spelling
     * of "yes, on purpose". */
    mg_free((void*)(uintptr_t)str->items, MG_MEMORY_TAG_STRING);
    str->items = NULL;
    str->count = 0;
}

len mg_cstr_length(const char* cstr) {
    /* The NULL guard is the function's whole reason to exist over bare
     * strlen, which C leaves undefined on NULL: here NULL measures as 0, so
     * "no string" and "empty string" are the same length everywhere a cstr
     * is taken. */
    return cstr == NULL ? 0 : strlen(cstr);
}

b8 mg_str_empty(const mg_str str) {
    return mg_str_view_empty(mg_view_str(str));
}

b8 mg_str_view_empty(const mg_str_view view) {
    /* Both halves, because the two are not the same check: the out-of-memory
     * string has no items, and a view narrowed to nothing has items it spans
     * none of. */
    return view.items == NULL || view.count == 0;
}

const char* mg_str_to_cstr(const mg_str str) {
    return str.items;
}

const char* mg_str_view_to_cstr(const mg_str_view view) {
    return view.items;
}

mg_str mg_str_append(const mg_str_view str, const mg_str_view append) {
    mg_str out = {NULL, 0};

    char* items = mg_str_alloc(str.count + append.count);
    if (items == NULL) {
        return out;
    }

    /* Guarded for the same reason as in mg_str_new: either half may be empty,
     * and an empty view's items may be NULL. */
    if (str.count > 0) {
        mg_mem_copy(items, str.items, str.count);
    }
    if (append.count > 0) {
        mg_mem_copy(items + str.count, append.items, append.count);
    }

    out.items = items;
    out.count = str.count + append.count;
    return out;
}

mg_str_view mg_str_trim(const mg_str_view str) {
    return mg_str_trim_right(mg_str_trim_left(str));
}

mg_str_view mg_str_trim_left(const mg_str_view str) {
    len start = 0;
    while (start < str.count && mg_is_space(mg_chars(str)[start])) {
        start++;
    }

    const mg_str_view view = {mg_chars(str) + start, sizeof(char), str.count - start};
    return view;
}

mg_str_view mg_str_trim_right(const mg_str_view str) {
    len count = str.count;
    while (count > 0 && mg_is_space(mg_chars(str)[count - 1])) {
        count--;
    }

    const mg_str_view view = {str.items, sizeof(char), count};
    return view;
}

b8 mg_str_eq(const mg_str_view a, const mg_str_view b) {
    if (a.count != b.count) {
        return FALSE;
    }

    for (len i = 0; i < a.count; i++) {
        if (mg_chars(a)[i] != mg_chars(b)[i]) {
            return FALSE;
        }
    }

    return TRUE;
}

b8 mg_str_contains(const mg_str_view str, const mg_str_view substr) {
    if (substr.count > str.count)
        return FALSE;

    for (len i = 0; i <= str.count - substr.count; i++) {
        const mg_str_view window = {mg_chars(str) + i, sizeof(char), substr.count};
        if (mg_str_eq(window, substr)) {
            return TRUE;
        }
    }

    return FALSE;
}

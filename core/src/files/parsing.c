/**
 * @file parsing.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/files/parsing.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/files/parsing.h"
#include <ctype.h>
#include <errno.h>
#include <stdlib.h>

/* strto* and not ato*, which reports a conversion error the same way it
 * reports zero: not at all. Every checker below reads strto*'s whole report
 * -- end for "no digits at all" and "digits, then garbage", errno for a
 * value the widest type cannot hold -- and the bounds narrow that widest
 * parse to the caller's own type. The bounds arrive as parameters on
 * purpose: spelled inline, a comparison like value > U64_MAX is one the
 * compiler can prove never true and -Werror rejects as tautological. */

/**
 * @brief text as a non-negative number no wider than max, strictly.
 * @param text The whole number, or NULL.
 * @param max The widest value the caller's type holds.
 * @param out Where the value lands on success; untouched otherwise.
 * @return Whether text was such a number.
 */
static b8 mg_parse_unsigned(const char* text, const u64 max, u64* out) {
    if (text == NULL) {
        return FALSE;
    }

    /* strtoull accepts a leading minus and wraps the result around, so -1
     * would come back as U64_MAX: the one error it refuses to report. */
    const char* first = text;
    while (isspace((unsigned char)*first)) {
        first++;
    }
    if (*first == '-') {
        return FALSE;
    }

    char* end = NULL;
    errno = 0;
    const u64 value = strtoull(text, &end, 10);
    if (end == text || *end != '\0' || errno == ERANGE || value > max) {
        return FALSE;
    }
    *out = value;
    return TRUE;
}

/**
 * @brief text as a number between min and max, strictly.
 * @param text The whole number, or NULL.
 * @param min The lowest value the caller's type holds.
 * @param max The highest value the caller's type holds.
 * @param out Where the value lands on success; untouched otherwise.
 * @return Whether text was such a number.
 */
static b8 mg_parse_signed(const char* text, const i64 min, const i64 max, i64* out) {
    if (text == NULL) {
        return FALSE;
    }

    char* end = NULL;
    errno = 0;
    const i64 value = strtoll(text, &end, 10);
    if (end == text || *end != '\0' || errno == ERANGE || value < min || value > max) {
        return FALSE;
    }
    *out = value;
    return TRUE;
}

u8 mg_parse_u8(const char* text, const u8 fallback) {
    u64 value = 0;
    return mg_parse_unsigned(text, U8_MAX, &value) ? (u8)value : fallback;
}

u16 mg_parse_u16(const char* text, const u16 fallback) {
    u64 value = 0;
    return mg_parse_unsigned(text, U16_MAX, &value) ? (u16)value : fallback;
}

u32 mg_parse_u32(const char* text, const u32 fallback) {
    u64 value = 0;
    return mg_parse_unsigned(text, U32_MAX, &value) ? (u32)value : fallback;
}

u64 mg_parse_u64(const char* text, const u64 fallback) {
    u64 value = 0;
    return mg_parse_unsigned(text, U64_MAX, &value) ? value : fallback;
}

i8 mg_parse_i8(const char* text, const i8 fallback) {
    i64 value = 0;
    return mg_parse_signed(text, I8_MIN, I8_MAX, &value) ? (i8)value : fallback;
}

i16 mg_parse_i16(const char* text, const i16 fallback) {
    i64 value = 0;
    return mg_parse_signed(text, I16_MIN, I16_MAX, &value) ? (i16)value : fallback;
}

i32 mg_parse_i32(const char* text, const i32 fallback) {
    i64 value = 0;
    return mg_parse_signed(text, I32_MIN, I32_MAX, &value) ? (i32)value : fallback;
}

i64 mg_parse_i64(const char* text, const i64 fallback) {
    i64 value = 0;
    return mg_parse_signed(text, I64_MIN, I64_MAX, &value) ? value : fallback;
}

f32 mg_parse_f32(const char* text, const f32 fallback) {
    if (text == NULL) {
        return fallback;
    }

    char* end = NULL;
    errno = 0;
    const f32 value = strtof(text, &end);
    if (end == text || *end != '\0' || errno == ERANGE) {
        return fallback;
    }
    return value;
}

f64 mg_parse_f64(const char* text, const f64 fallback) {
    if (text == NULL) {
        return fallback;
    }

    char* end = NULL;
    errno = 0;
    const f64 value = strtod(text, &end);
    if (end == text || *end != '\0' || errno == ERANGE) {
        return fallback;
    }
    return value;
}

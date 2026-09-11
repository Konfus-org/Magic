/**
 * @file enums.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Printable enums: the mint that defines an enum and its name table
 * as one thing, and MG_GET_ENUM_NAME, the one way to read a value's spelling.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/primitives.h"

/* The per-entry expansions the mint hands its values list: one spells the
 * enumerator, the other spells its name. The trailing _ marks them the
 * mint's own; nothing else calls them. */
#define MG_ENUM_VALUE_(entry) entry,
#define MG_ENUM_NAME_(entry) #entry,

/**
 * @brief Mints a printable enum under name exactly: the typedef, its count
 * terminator, and name##_name, the lookup MG_GET_ENUM_NAME reads. The names
 * are stringized from the enumerators themselves, so the enum and its table
 * can never drift apart -- never keep a parallel name array by hand. The tag
 * and the typedef share name, so `enum name` spellings keep working. Minted
 * where the enum was declared, once per translation unit.
 * `result` in primitives.h stays a plain enum: it is not mg_ prefixed, and
 * this header stands on primitives.h.
 * @param name The minted type name, used EXACTLY as given.
 * @param count_name The terminator enumerator: one past the last value, and
 * the bound the lookup checks. A parameter because the existing spellings are
 * not derivable from name (MG_KEY_COUNT for mg_key_code).
 * @param values The list macro naming every enumerator in order, one X(...)
 * each, sequential from zero: X-macro style, taking the per-entry expansion
 * as its one argument.
 * @code
 * #define MG_APP_STATE_VALUES_(X)                                            \
 *     X(MG_APP_STATE_UNINITIALIZED)                                          \
 *     X(MG_APP_STATE_RUNNING)
 * MG_DEFINE_ENUM(mg_app_state, MG_APP_STATE_COUNT, MG_APP_STATE_VALUES_);
 *
 * MG_INFO("state: %s", MG_GET_ENUM_NAME(mg_app_state, app->state));
 * @endcode
 */
#define MG_DEFINE_ENUM(name, count_name, values)                                                   \
    typedef enum name { values(MG_ENUM_VALUE_) count_name } name;                                  \
                                                                                                   \
    /* The u64 index spelling every collection uses, so any integer a code     \
     * travels as -- enum, u32, int -- arrives without narrowing. */           \
    MG_MAYBE_UNUSED static inline const char* name##_name(const u64 value) {                       \
        static const char* const names[] = {values(MG_ENUM_NAME_)};                                \
        return value < (u64)(count_name) ? names[value] : "?";                                     \
    }                                                                                              \
                                                                                                   \
    /* Eats the call site's semicolon; retyping a name to the same type is legal C11. */           \
    typedef name name

/**
 * @brief The spelling of value under its minted enum type: "MG_KEY_W" for
 * MG_KEY_W, or "?" for a value past the count, which is where custom codes
 * minted after a family's COUNT land.
 * @param type The minted enum type the value belongs to.
 * @param value The value to name; any integer spelling of it works.
 */
#define mg_get_enum_name(type, value) type##_name((u64)(value))

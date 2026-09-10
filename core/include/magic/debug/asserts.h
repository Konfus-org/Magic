/**
 * @file asserts.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Static and runtime assertions, the debug trap behind them, and the
 * unimplemented-stub macros built on that same trap.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include <stdlib.h>

/* _Static_assert is C-only; C++ consumers, the test suite among them, take
 * static_assert. */
#if defined(__cplusplus)
    /** @brief Static assertion */
    #define STATIC_ASSERT static_assert
#elif defined(__clang__) || defined(__GNUC__)
    /** @brief Static assertion */
    #define STATIC_ASSERT _Static_assert
#else
    /** @brief Static assertion */
    #define STATIC_ASSERT static_assert
#endif

/* Each spelling lowers to the architecture's trap instruction, so the break
 * lands on the failing line. With no debugger attached all four end the
 * process. clang is tested before MSVC because clang on the MSVC ABI defines
 * _MSC_VER too. */
#if defined(__clang__)
    /** @brief Traps into an attached debugger, or ends the process. */
    #define MG_DEBUG_BREAK() __builtin_debugtrap()
#elif defined(_MSC_VER)
    /** @brief Traps into an attached debugger, or ends the process. */
    #define MG_DEBUG_BREAK() __debugbreak()
#elif defined(__GNUC__)
    /** @brief Traps into an attached debugger, or ends the process. */
    #define MG_DEBUG_BREAK() __builtin_trap()
#else
    /** @brief Traps into an attached debugger, or ends the process. */
    #define MG_DEBUG_BREAK() abort()
#endif

/* Set from the build option of the same name. Always defined, never left to
 * the preprocessor's implicit 0, because -Wundef is an error here. */
#ifndef MG_ASSERT_ENABLED
    #define MG_ASSERT_ENABLED 1
#endif

/* The report-and-trap half of an assertion, split out so the RETURN_IF
 * variants can reuse it. Compiled out with the rest. */
#if MG_ASSERT_ENABLED == 1
    #define MG_ASSERT_FAILED_(...)                                                              \
        do {                                                                                       \
            MG_FATAL("Assertion failed: " __VA_ARGS__);                                         \
            MG_DEBUG_BREAK();                                                                   \
        } while (0)
#else
    #define MG_ASSERT_FAILED_(...)                                                              \
        do {                                                                                       \
            if (0) {                                                                               \
                MG_FATAL("Assertion failed: " __VA_ARGS__);                                     \
            }                                                                                      \
        } while (0)
#endif

#if MG_ASSERT_ENABLED == 1
    /**
     * @brief Reports the message and traps when cond is false.
     * Gated by MG_ASSERT_ENABLED alone; NDEBUG plays no part.
     */
    #define MG_ASSERT(cond, ...)                                                                \
        do {                                                                                       \
            if (cond) { /*do nothing*/                                                             \
            }                                                                                      \
            else {                                                                                 \
                MG_ASSERT_FAILED_(__VA_ARGS__);                                                 \
            }                                                                                      \
        } while (0)
#else
    /**
     * @brief Runtime assertion, compiled out.
     * The dead branch keeps cond and the message parsed and type-checked, so a
     * variable named only in an assertion draws no unused-variable error.
     */
    #define MG_ASSERT(cond, ...)                                                                \
        do {                                                                                       \
            if (0) {                                                                               \
                if (!(cond))                                                                       \
                    MG_ASSERT_FAILED_(__VA_ARGS__);                                             \
            }                                                                                      \
        } while (0)
#endif

/* In both macros below, `(cond) ? 1 : 0` evaluates the condition exactly once
 * and is the cast-free spelling of "is this true" for every scalar code,
 * pointers and floats included. */

/** @brief Asserts a precondition, and returns from a void function if it fails. */
#define MG_ASSERT_RETURN_IF(cond, ...)                                                          \
    do {                                                                                           \
        const int __mg_early_out = ((cond) ? 1 : 0);                                            \
        if (!__mg_early_out) {                                                                  \
            MG_ASSERT_FAILED_(__VA_ARGS__);                                                     \
            return;                                                                                \
        }                                                                                          \
    } while (0)

/** @brief Asserts a precondition, and returns val if it fails. */
#define MG_ASSERT_RETURN_VAL_IF(cond, val, ...)                                                 \
    do {                                                                                           \
        const int __mg_early_out = ((cond) ? 1 : 0);                                            \
        if (!__mg_early_out) {                                                                  \
            MG_ASSERT_FAILED_(__VA_ARGS__);                                                     \
            return val;                                                                            \
        }                                                                                          \
    } while (0)

/* Argument-count dispatch: the real arguments push MG_UNUSED_<n>_ into the
 * NAME slot, and the picker hands back whatever landed there. The trailing 0
 * sentinel keeps the picker's own ... non-empty at one argument, which
 * -Wpedantic requires. */
#define MG_EXPAND_(x) x

#define MG_UNUSED_1_(a) ((void)(a))
#define MG_UNUSED_2_(a, b) ((void)(a), (void)(b))
#define MG_UNUSED_3_(a, b, c) ((void)(a), (void)(b), (void)(c))
#define MG_UNUSED_4_(a, b, c, d) ((void)(a), (void)(b), (void)(c), (void)(d))
#define MG_UNUSED_5_(a, b, c, d, e) ((void)(a), (void)(b), (void)(c), (void)(d), (void)(e))
#define MG_UNUSED_6_(a, b, c, d, e, f)                                                          \
    ((void)(a), (void)(b), (void)(c), (void)(d), (void)(e), (void)(f))
#define MG_UNUSED_7_(a, b, c, d, e, f, g)                                                       \
    ((void)(a), (void)(b), (void)(c), (void)(d), (void)(e), (void)(f), (void)(g))
#define MG_UNUSED_8_(a, b, c, d, e, f, g, h)                                                    \
    ((void)(a), (void)(b), (void)(c), (void)(d), (void)(e), (void)(f), (void)(g), (void)(h))

#define MG_UNUSED_PICK_(_1, _2, _3, _4, _5, _6, _7, _8, NAME, ...) NAME

/** @brief Marks one to eight parameters as deliberately unused. */
#define MG_UNUSED(...)                                                                          \
    MG_EXPAND_(MG_UNUSED_PICK_(                                                              \
        __VA_ARGS__,                                                                               \
        MG_UNUSED_8_,                                                                           \
        MG_UNUSED_7_,                                                                           \
        MG_UNUSED_6_,                                                                           \
        MG_UNUSED_5_,                                                                           \
        MG_UNUSED_4_,                                                                           \
        MG_UNUSED_3_,                                                                           \
        MG_UNUSED_2_,                                                                           \
        MG_UNUSED_1_,                                                                           \
        0)(__VA_ARGS__))

/**
 * @brief Reports the calling function as unimplemented and traps.
 * The whole body of a stub not yet written, so calling one is loud rather than
 * quietly wrong.
 */
#define MG_NOT_IMPLEMENTED()                                                                    \
    do {                                                                                           \
        MG_FATAL("%s is not implemented", __func__);                                            \
        MG_DEBUG_BREAK();                                                                       \
    } while (0)

/**
 * @brief MG_NOT_IMPLEMENTED for a stub that returns type.
 * The value is never read; a non-void function just has to return something.
 */
#define MG_NOT_IMPLEMENTED_RETURN(type)                                                         \
    do {                                                                                           \
        type __mg_unreached = {0};                                                              \
        MG_NOT_IMPLEMENTED();                                                                   \
        return __mg_unreached;                                                                  \
    } while (0)

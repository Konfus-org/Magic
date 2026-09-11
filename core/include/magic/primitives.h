/**
 * @file primitives.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The fixed-width scalar types every Magic header is written in, and the
 * limits and helper macros that go with them.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/debug/asserts.h"
#include <stdlib.h>

#define U64_MAX 18446744073709551615ULL
#define U32_MAX 4294967295U
#define U16_MAX 65535U
#define U8_MAX 255U
#define U64_MIN 0ULL
#define U32_MIN 0U
#define U16_MIN 0U
#define U8_MIN 0U
#define I8_MAX 127
#define I16_MAX 32767
#define I32_MAX 2147483647
#define I64_MAX 9223372036854775807LL

#define I8_MIN (-I8_MAX - 1)
#define I16_MIN (-I16_MAX - 1)
#define I32_MIN (-I32_MAX - 1)
#define I64_MIN (-I64_MAX - 1)

#define TRUE 1
#define FALSE 0

#define EPSILON 1e-6

/** @brief Returns the absolute value of a numbers. */
#define absolute(x) _Generic((x), float: fabsf(x), default: fabs(x))

/** @brief Returns the maximum of two numbers. */
#define maximum(a, b) _Generic((a) + (b), float: fmaxf(a, b), default: fmax(a, b))

/** @brief Compares two numbers for equality within a small margin of error. */
#define fequivalent(a, b)                                                                          \
    (((a) == (b)) ? TRUE : (absolute((a) - (b)) <= (EPSILON) * maximum(absolute(a), absolute(b))))

/** @brief Size of one kilobyte in bytes. */
#define KILOBYTE 1024
/** @brief Size of one megabyte in bytes. */
#define MEGABYTE (KILOBYTE * KILOBYTE)
/** @brief Size of one gigabyte in bytes. */
#define GIGABYTE (MEGABYTE * KILOBYTE)

/** @brief Bytes from kilobytes. */
#define KILOBYTES(kilos) ((kilos) * KILOBYTE)
/** @brief Bytes from megabytes. */
#define MEGABYTES(megs) ((megs) * MEGABYTE)
/** @brief Bytes from gigabytes. */
#define GIGABYTES(gigs) ((gigs) * GIGABYTE)

/**
 * @brief Marks a definition the translation unit is free never to use.
 * The collection mints put it on every function they generate: a mint is the
 * whole API by design, and the compiler warns about unused static functions
 * minted in a .c -- from a header it already stays quiet.
 */
#if defined(__GNUC__) || defined(__clang__)
    #define MG_MAYBE_UNUSED __attribute__((unused))
#else
    #define MG_MAYBE_UNUSED
#endif

/**
 * @brief Names a compound literal's type, the one spelling that compiles as
 * both C and C++.
 * C spells one (type){...} and C++ spells it type{...}; the parenthesised form
 * is a cast in C++ and does not compile.
 */
#ifdef __cplusplus
    #define LITERAL(type) type
#else
    #define LITERAL(type) (type)
#endif

/** @brief Builds an r64 from an offset and a size. */
#define range(offset, size) (LITERAL(r64) {(u64)(offset), (u64)(size)})

/** @brief Unsigned 8-bit integer */
typedef unsigned char u8;

/** @brief Unsigned 16-bit integer */
typedef unsigned short u16;

/** @brief Unsigned 32-bit integer */
typedef unsigned int u32;

/** @brief Unsigned 64-bit integer */
typedef unsigned long long u64;

/** @brief Signed 8-bit integer */
typedef signed char i8;

/** @brief Signed 16-bit integer */
typedef signed short i16;

/** @brief Signed 32-bit integer */
typedef signed int i32;

/** @brief Signed 64-bit integer */
typedef signed long long i64;

/** @brief 32-bit floating point number */
typedef float f32;

/** @brief 64-bit floating point number */
typedef double f64;

/** @brief Unsigned 8-bit integer */
typedef unsigned char byte;

/** @brief Pointer as an int AKA the address. */
typedef intptr_t addr;

/** @brief Used to index into collections. */
typedef u64 ind;

/** @brief A size, of arrays, lists, etc. */
typedef u64 len;

/** @brief A time stamp in 64 bits. */
typedef f64 ts;

/** @brief An error code, 0 is no error. */
typedef int err_code;

/** @brief The result of a function that can fail. */
typedef enum result {
    FAILURE = 0,
    OK = 1,
} result;

/* _Bool is a C keyword that does not exist in C++, where the type is spelled
 * bool. The two are laid out identically on every ABI Magic targets, which is
 * what lets a struct holding one cross the C/C++ boundary. */
#ifdef __cplusplus
/** @brief 8-bit boolean type */
typedef bool b8;
#else
/** @brief 8-bit boolean type */
typedef _Bool b8;
#endif

/** @brief 32-bit boolean type, for APIs which require it */
typedef int b32;

/** @brief A size in 64-bit units. */
typedef struct s64 {
    u64 width;
    u64 height;
} s64;

/** @brief A size in 32-bit units. */
typedef struct s32 {
    u32 width;
    u32 height;
} s32;

/** @brief A range in 64-bit units, typically of memory. */
typedef struct r64 {
    u64 offset;
    u64 size;
} r64;

/** @brief A range in 32-bit units, typically of memory. */
typedef struct r32 {
    u32 offset;
    u32 size;
} r32;

STATIC_ASSERT(sizeof(u8) == 1, "Expected u8 to be 1 byte.");
STATIC_ASSERT(sizeof(u16) == 2, "Expected u16 to be 2 bytes.");
STATIC_ASSERT(sizeof(u32) == 4, "Expected u32 to be 4 bytes.");
STATIC_ASSERT(sizeof(u64) == 8, "Expected u64 to be 8 bytes.");
STATIC_ASSERT(sizeof(i8) == 1, "Expected i8 to be 1 byte.");
STATIC_ASSERT(sizeof(i16) == 2, "Expected i16 to be 2 bytes.");
STATIC_ASSERT(sizeof(i32) == 4, "Expected i32 to be 4 bytes.");
STATIC_ASSERT(sizeof(i64) == 8, "Expected i64 to be 8 bytes.");
STATIC_ASSERT(sizeof(f32) == 4, "Expected f32 to be 4 bytes.");
STATIC_ASSERT(sizeof(f64) == 8, "Expected f64 to be 8 bytes.");
STATIC_ASSERT(sizeof(b8) == 1, "Expected b8 to be 1 byte.");

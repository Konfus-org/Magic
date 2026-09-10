/**
 * @file parsing.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Numbers out of text, one function per primitive. Every parse is
 * strict and total: the whole text must be one base-10 number that fits the
 * type, and anything else -- NULL, garbage, trailing characters, a value out
 * of range -- comes back as the caller's fallback instead of an error.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief Parses text as a u8.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT u8 mg_parse_u8(const char* text, u8 fallback);

/**
 * @brief Parses text as a u16.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT u16 mg_parse_u16(const char* text, u16 fallback);

/**
 * @brief Parses text as a u32.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT u32 mg_parse_u32(const char* text, u32 fallback);

/**
 * @brief Parses text as a u64.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT u64 mg_parse_u64(const char* text, u64 fallback);

/**
 * @brief Parses text as an i8.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT i8 mg_parse_i8(const char* text, i8 fallback);

/**
 * @brief Parses text as an i16.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT i16 mg_parse_i16(const char* text, i16 fallback);

/**
 * @brief Parses text as an i32.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT i32 mg_parse_i32(const char* text, i32 fallback);

/**
 * @brief Parses text as an i64.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT i64 mg_parse_i64(const char* text, i64 fallback);

/**
 * @brief Parses text as an f32; decimal or scientific notation, like C's own
 * literals.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT f32 mg_parse_f32(const char* text, f32 fallback);

/**
 * @brief Parses text as an f64; decimal or scientific notation, like C's own
 * literals.
 * @param text The whole number, nothing after it; leading whitespace is fine.
 * @param fallback What malformed or out-of-range text comes back as.
 * @return The parsed value, or fallback.
 */
MG_EXPORT f64 mg_parse_f64(const char* text, f64 fallback);

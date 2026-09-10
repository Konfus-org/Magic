/**
 * @file generators.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Randomness, from whichever generator the platform makes available.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief Fills count bytes at bytes with platform entropy.
 * The primitive the rest of this header is built on; exported so a gem draws
 * from the same source core does.
 * @param bytes Must not be NULL when count is non-zero.
 * @param count Zero is nothing to do, and succeeds.
 * @return False if the platform's generator failed; the buffer then holds
 * nothing worth reading.
 */
MG_EXPORT b8 mg_generate_random_bytes(mg_byte_list bytes, len count);

/**
 * @brief A random u32.
 * @return The draw, or 0 if the generator failed. 0 is also a legitimate draw;
 * a caller that must tell the two apart uses mg_generate_random_bytes.
 */
MG_EXPORT u32 mg_generate_random_u32(void);

/** @brief A random u64. Carries the same 0 caveat as mg_generate_random_u32. */
MG_EXPORT u64 mg_generate_random_u64(void);

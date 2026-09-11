/**
 * @file stats.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Per-tag accounting of the engine heap: what mg_alloc has handed
 * out and mg_free has taken back, and the tags every allocation is filed under.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/enums.h"
#include "magic/export.h"
#include "magic/primitives.h"

/** @brief Every memory tag, for the mg_memory_tag mint below. */
#define MG_MEMORY_TAG_VALUES_(X)                                                                   \
    X(MG_MEMORY_TAG_UNKNOWN)                                                                       \
    X(MG_MEMORY_TAG_ARRAY)                                                                         \
    X(MG_MEMORY_TAG_LIST)                                                                          \
    X(MG_MEMORY_TAG_STRING)                                                                        \
    X(MG_MEMORY_TAG_APPLICATION)                                                                   \
    X(MG_MEMORY_TAG_EVENT)                                                                         \
    X(MG_MEMORY_TAG_LOGGING)                                                                       \
    X(MG_MEMORY_TAG_PLATFORM)                                                                      \
    X(MG_MEMORY_TAG_INPUT)                                                                         \
    X(MG_MEMORY_TAG_RENDERING)                                                                     \
    X(MG_MEMORY_TAG_DYNAMIC_LIBRARY)                                                               \
    X(MG_MEMORY_TAG_TEXTURE)                                                                       \
    X(MG_MEMORY_TAG_MATERIAL)                                                                      \
    X(MG_MEMORY_TAG_MODEL)

/**
 * @brief What an allocation is for, so usage reads as a budget by system
 * rather than one opaque total. UNKNOWN is the tag of code that has not
 * chosen one, not an error.
 */
MG_DEFINE_ENUM(mg_memory_tag, MG_MEMORY_TAG_COUNT, MG_MEMORY_TAG_VALUES_);

/** @brief Running byte totals; what is live now is allocated minus deallocated. */
typedef struct mg_memory_stats {
    len allocated; /**< Bytes handed out since the process began. */
    len deallocated; /**< Bytes taken back since the process began. */
} mg_memory_stats;

/** @brief The whole ledger: one entry per tag, and the sum across them. */
typedef struct mg_memory_usage {
    mg_memory_stats tagged[MG_MEMORY_TAG_COUNT];
    mg_memory_stats total;
} mg_memory_usage;

/**
 * @brief Gets current memory usage.
 * @return A copy of the whole ledger, per tag and in total.
 */
MG_EXPORT mg_memory_usage mg_get_mem_usage(void);

/**
 * @brief Gets current memory usage for a specific tag.
 * @param tag The tag to read.
 * @return That tag's running totals, or zeroes for a tag out of range.
 */
MG_EXPORT mg_memory_stats mg_get_mem_usage_for(enum mg_memory_tag tag);

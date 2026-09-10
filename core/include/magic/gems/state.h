/**
 * @file state.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief How a gem registers itself: identity, ABI, and lifecycle hooks. The
 * slots a gem fills live in magic/gems/slots.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/gems/gem.h"
#include "magic/platform/shared_lib.h"

/**
 * @brief Layout version of everything a gem publishes. Bump on any shape
 * change to mg_gem_desc or the symbol types; checked before anything else.
 */
#define MG_GEM_ABI_VERSION 1u

/** @brief The export a gem is recognized by; a library without it is not a gem. */
#define MG_GEM_ENTRY_POINT "mg_gem_describe"

/**
 * @brief Registers a gem: identity and lifecycle hooks, as one export.
 * Goes at file scope in exactly one .c of the gem; any hook may be NULL.
 * The slots a gem fills go in a separate MG_GEM_FILL_SLOTS block.
 * @code
 * MG_PROVIDE_GEM("win32", "1.0", "Win32 platform backend", "Jeremy Hummel",
 *                    on_load, on_reload, on_reloaded, on_unload)
 * @endcode
 */
#define MG_PROVIDE_GEM(                                                                                                     \
    gem_name,                                                                                                               \
    gem_version,                                                                                                            \
    gem_description,                                                                                                        \
    gem_author,                                                                                                             \
    load,                                                                                                                   \
    reload,                                                                                                                 \
    reloaded,                                                                                                               \
    unload)                                                                                                                 \
    MG_LIB_EXPORT const mg_gem_desc* mg_gem_describe(void);                                                                 \
    MG_LIB_EXPORT const mg_gem_desc* mg_gem_describe(void) {                                                                \
        /* Filled on call rather than initialized: viewing a literal is a                                                   \
         * function call, and no function call can initialize an object of                                                  \
         * static storage duration in C. Static because the caller keeps the                                                \
         * pointer; writing the same values again is harmless, and the loader                                               \
         * asks exactly once per load. */                                                                                   \
        static mg_gem_desc desc;                                                                                            \
        desc.abi_version = MG_GEM_ABI_VERSION;                                                                              \
        desc.name = mg_view_cstr(gem_name);                                                                                 \
        desc.version = mg_view_cstr(gem_version);                                                                           \
        desc.description = mg_view_cstr(gem_description);                                                                   \
        desc.author = mg_view_cstr(gem_author);                                                                             \
        desc.hooks.on_load = (load);                                                                                        \
        desc.hooks.on_reload = (reload);                                                                                    \
        desc.hooks.on_reloaded = (reloaded);                                                                                \
        desc.hooks.on_unload = (unload);                                                                                    \
        return &desc;                                                                                                       \
    }

/** @brief The function provided by the MG_GEM_ENTRY_POINT. */
typedef const mg_gem_desc* (*mg_gem_describe_fn)(void);

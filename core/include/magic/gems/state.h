/**
 * @file state.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief How a gem registers itself: identity, ABI, dependencies and
 * lifecycle hooks. The slots a gem fills live in magic/gems/slots.h.
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
#define MG_GEM_ABI_VERSION 2u

/** @brief The export a gem is recognized by; a library without it is not a gem. */
#define MG_GEM_ENTRY_POINT "mg_gem_describe"

/**
 * @brief The gems that must be loaded, and whose on_load must have run,
 * before this one is. Goes in MG_PROVIDE_GEM's requires slot, naming each
 * dependency by the name its own MG_PROVIDE_GEM gives it; matching is exact.
 * A gem naming a dependency no gem in the folder answers to is not loaded at
 * all, so a missing dependency is a load-time complaint rather than a null
 * call later. Past MG_MAX_GEM_DEPENDENCIES names, the extras are dropped.
 * @code
 * MG_PROVIDE_GEM("SDL_platform", "1.0", "...", "...",
 *                MG_GEM_REQUIRES("SDL_base"),
 *                on_load, NULL, NULL, on_unload)
 * @endcode
 */
#define MG_GEM_REQUIRES(...)                                                                                            \
    {                                                                                                                   \
        /* static const: the names are literals in this image, and the views                                            \
         * below borrow them for as long as the image is mapped. */                                                     \
        static const char* const required_[] = {__VA_ARGS__};                                                           \
        const len asked_ = (len)(sizeof(required_) / sizeof(required_[0]));                                             \
        desc.dependency_count = asked_ < MG_MAX_GEM_DEPENDENCIES ? asked_ : MG_MAX_GEM_DEPENDENCIES;                    \
        for (len i_ = 0; i_ < desc.dependency_count; i_++) {                                                            \
            desc.dependencies[i_] = mg_view_cstr(required_[i_]);                                                        \
        }                                                                                                               \
    }

/**
 * @brief What a gem that stands on its own puts in MG_PROVIDE_GEM's requires
 * slot. The common case: a gem needs nothing loaded before it.
 */
#define MG_GEM_REQUIRES_NOTHING desc.dependency_count = 0;

/**
 * @brief Registers a gem: identity, dependencies and lifecycle hooks, as one
 * export. Goes at file scope in exactly one .c of the gem; any hook may be
 * NULL. The slots a gem fills go in a separate MG_GEM_FILL_SLOTS block.
 * @param requires MG_GEM_REQUIRES(...) or MG_GEM_REQUIRES_NOTHING.
 * @code
 * MG_PROVIDE_GEM("win32", "1.0", "Win32 platform backend", "Jeremy Hummel",
 *                MG_GEM_REQUIRES_NOTHING,
 *                on_load, on_reload, on_reloaded, on_unload)
 * @endcode
 */
#define MG_PROVIDE_GEM(                                                                                                 \
    gem_name,                                                                                                           \
    gem_version,                                                                                                        \
    gem_description,                                                                                                    \
    gem_author,                                                                                                         \
    gem_requires,                                                                                                       \
    load,                                                                                                               \
    reload,                                                                                                             \
    reloaded,                                                                                                           \
    unload)                                                                                                             \
    MG_LIB_EXPORT const mg_gem_desc* mg_gem_describe(void);                                                             \
    MG_LIB_EXPORT const mg_gem_desc* mg_gem_describe(void) {                                                            \
        /* Filled on call rather than initialized: viewing a literal is a                                               \
         * function call, and no function call can initialize an object of                                              \
         * static storage duration in C. Static because the caller keeps the                                            \
         * pointer; writing the same values again is harmless, and the loader                                           \
         * asks exactly once per load. */                                                                               \
        static mg_gem_desc desc;                                                                                        \
        desc.abi_version = MG_GEM_ABI_VERSION;                                                                          \
        desc.name = mg_view_cstr(gem_name);                                                                             \
        desc.version = mg_view_cstr(gem_version);                                                                       \
        desc.description = mg_view_cstr(gem_description);                                                               \
        desc.author = mg_view_cstr(gem_author);                                                                         \
        gem_requires                                                                                                    \
        desc.hooks.on_load = (load);                                                                                    \
        desc.hooks.on_reload = (reload);                                                                                \
        desc.hooks.on_reloaded = (reloaded);                                                                            \
        desc.hooks.on_unload = (unload);                                                                                \
        return &desc;                                                                                                   \
    }

/** @brief The function provided by the MG_GEM_ENTRY_POINT. */
typedef const mg_gem_desc* (*mg_gem_describe_fn)(void);

/**
 * @file gem.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief What a gem is: a loaded library plus an identity, a lifecycle, and
 * the gems it needs standing before it.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/collections/string.h"
#include "magic/platform/shared_lib.h"

/** @brief How many gems one gem may name as its dependencies.*/
#define MG_MAX_GEM_DEPENDENCIES 8

// Forward declared to avoid a cycle through app.h.
typedef struct mg_app mg_app;

/**
 * @brief The lifecycle hooks a gem provides. Any of them may be NULL.
 * The state lists are typed spellings of the same mg_buffer they always
 * were, so this is the same ABI a gem built before the mints speaks.
 */
typedef struct mg_gem_callbacks {
    void (*on_load)(mg_app* owner);
    void (*on_reload)(mg_app* owner, mg_byte_list state);
    void (*on_reloaded)(mg_app* owner, mg_byte_list restore_state);
    void (*on_unload)(mg_app* owner);
} mg_gem_callbacks;

/**
 * @brief Everything a gem says about itself through its one entry point.
 * The views borrow literals in the gem's own image, so none survive it.
 */
typedef struct mg_gem_desc {
    u32 abi_version; /**< Must equal MG_GEM_ABI_VERSION to load. */
    mg_str_view name;
    mg_str_view version;
    mg_str_view description;
    mg_str_view author;
    /** @brief The names of the gems that must be loaded before this one. */
    mg_str_view dependencies[MG_MAX_GEM_DEPENDENCIES];
    len dependency_count; /**< How many of dependencies are filled in. */
    mg_gem_callbacks hooks; /**< Lifecycle hooks; any of them may be NULL. */
} mg_gem_desc;

/**
 * @brief A runtime loaded library that provides callbacks to the engine.
 * lib and desc describe the image currently open and do not survive it; a
 * reload replaces both. path and owner outlive the image, which is what makes
 * reloading in place possible.
 */
typedef struct mg_gem {
    mg_shared_lib lib;
    mg_gem_desc desc;
    mg_str path; /**< File this gem was loaded from. Owned; freed on unload. */
    mg_app* owner; /**< App the gem was loaded into. Borrowed, and outlives the gem. */
} mg_gem;

/**
 * @brief A gem, by pointer. What an app's gem list holds, so gems keep
 * their addresses across the appends that grow the list; the list runs no
 * destructor over them, so mg_unload_gems is the owning teardown.
 */
typedef mg_gem* mg_gem_ptr;

MG_DEFINE_NAMED_LIST(mg_gem_ptr, mg_gem_list, NULL);

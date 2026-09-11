/**
 * @file loading.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Loading, reloading and unloading gems.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/gems/state.h"

/**
 * @brief Loads a gem from a file path.
 * Takes the gem at its word about its dependencies: one gem loaded by itself
 * has no folder to resolve them against, so nothing is checked here. Only
 * mg_load_gems orders a set.
 * @param owner Mutable because it is what every mg_gem_api hook is handed.
 * @return The gem, or NULL on failure.
 */
mg_gem* mg_load_gem(const char* path, mg_app* owner);

/** @brief Unloads a gem from memory and frees it. */
void mg_unload_gem(mg_gem* gem);

/**
 * @brief Reloads gem in place: unbinds it, closes its image, reopens the file
 * and binds what the new image provides.
 * Fires on_reload and on_reloaded; on_unload and on_load do not run. A failed
 * reload leaves the gem unbound and zeroed, with its path kept for another
 * attempt.
 */
void mg_reload_gem(mg_gem* gem);

/**
 * @brief Loads every gem in a folder into owner, in dependency order: every
 * gem is opened and asked what it needs before any is stood up, then each is
 * bound and its on_load fired only once every gem it named with
 * MG_GEM_REQUIRES has had its own. A gem whose dependencies cannot all be met
 * -- one names a gem the folder has none of, or two name each other -- is
 * reported and left unloaded, along with everything that needed it.
 * @return The list of gems, in the order they were stood up. The caller owns
 * it; hand it to mg_unload_gems.
 */
mg_gem_list mg_load_gems(const char* path, mg_app* owner);

/**
 * @brief Unloads every gem in gems and empties the list. Back to front, so a
 * gem is torn down while everything it was standing on is still up -- the
 * mirror of the order mg_load_gems stood them up in.
 */
void mg_unload_gems(mg_gem_list* gems);

/**
 * @brief Reloads every gem in the list, in place and in load order, so the
 * list is as valid afterwards as it was before, and a gem's dependencies see
 * their new images before it sees its own.
 */
void mg_reload_gems(mg_gem_list* gems);

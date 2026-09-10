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
 * @brief Loads every gem in a folder into owner.
 * @return The list of gems. The caller owns it; hand it to mg_unload_gems.
 */
mg_gem_list mg_load_gems(const char* path, mg_app* owner);

/** @brief Unloads every gem in gems and empties the list. */
void mg_unload_gems(mg_gem_list* gems);

/**
 * @brief Reloads every gem in the list, in place and in load order, so the
 * list is as valid afterwards as it was before.
 */
void mg_reload_gems(mg_gem_list* gems);

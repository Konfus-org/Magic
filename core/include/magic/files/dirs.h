/**
 * @file dirs.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Directory-related functions and utilities: thin wrappers over the
 * platform's file slots, so an app calls the engine and the loaded gem
 * answers.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/string.h"
#include "magic/export.h"

/**
 * @brief Reads the contents of a directory at the specified path, optionally
 * filtering by a pattern: NULL or empty keeps every name, anything else keeps
 * the names that contain it. Each name is an owned string the caller frees,
 * then the list.
 */
MG_EXPORT mg_str_list mg_read_dir(const char* path, const char* filter);

/**
 * @brief Reads the contents of a directory recursively at the specified
 * path, optionally filtering by a pattern. NOT IMPLEMENTED yet: recursion is
 * missing, so only the top level comes back.
 */
MG_EXPORT mg_str_list mg_read_dir_recursive(const char* path, const char* filter);

/**
 * @brief Creates a directory at the specified path. NOT IMPLEMENTED yet: no
 * platform slot creates directories, so nothing is created.
 */
MG_EXPORT b8 mg_create_dir(const char* path);

/**
 * @brief Deletes a directory at the given path if it exists. NOT IMPLEMENTED
 * yet: no platform slot deletes directories, so nothing is deleted.
 */
MG_EXPORT b8 mg_delete_dir(const char* path);

/** @brief Checks if a directory exists at the given path. */
MG_EXPORT b8 mg_dir_exists(const char* path);

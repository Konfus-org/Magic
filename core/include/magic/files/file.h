/**
 * @file file.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief File-related functions and utilities: thin wrappers over the
 * platform's file slots, so an app calls the engine and the loaded gem
 * answers.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/collections/string.h"
#include "magic/export.h"

/**
 * @brief Reads a text file and returns its contents as a string. Free with
 * mg_str_free; a failed read comes back as the empty string.
 */
MG_EXPORT mg_str mg_read_txt_file(const char* path);

/**
 * @brief Reads a binary file and returns its contents as a byte list. Free
 * with mg_byte_list_free; a failed read comes back empty.
 */
MG_EXPORT mg_byte_list mg_read_bin_file(const char* path);

/**
 * @brief Creates a new file at the specified path. NOT IMPLEMENTED yet: no
 * platform slot creates files, so nothing is created.
 */
MG_EXPORT b8 mg_create_file(const char* path);

/** @brief Copies a file from the source path to the destination path. */
MG_EXPORT b8 mg_copy_file(const char* src_path, const char* dest_path);

/** @brief Deletes a file at the given path if it exists. */
MG_EXPORT b8 mg_delete_file(const char* path);

/** @brief Checks if a file exists at the given path. */
MG_EXPORT b8 mg_file_exists(const char* path);

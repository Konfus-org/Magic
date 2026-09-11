/**
 * @file sdl_files.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Files: the platform's filesystem, reached through SDL.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "sdl_platform.h"
#include <SDL3/SDL.h>

/**
 * @brief gs_platform_copy_file via SDL.
 * @param from The file to copy.
 * @param to Where the copy goes, overwritten if it exists.
 * @return True on success.
 */
b8 sdl_copy_file(const char* from, const char* to) {
    return SDL_CopyFile(from, to) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_delete_file via SDL.
 * @param path The file to delete.
 * @return True on success.
 */
b8 sdl_delete_file(const char* path) {
    return SDL_RemovePath(path) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_move_file via SDL.
 * @param from The file to move.
 * @param to Where it goes, overwritten if it exists.
 * @return True on success.
 */
b8 sdl_move_file(const char* from, const char* to) {
    return SDL_RenamePath(from, to) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_file_exists via SDL.
 * @param path The path to check.
 * @return True when a file sits at path; a directory there is not one.
 */
b8 sdl_file_exists(const char* path) {
    SDL_PathInfo info;
    return SDL_GetPathInfo(path, &info) && info.type == SDL_PATHTYPE_FILE ? TRUE : FALSE;
}

/**
 * @brief gs_platform_dir_exists via SDL.
 * @param path The path to check.
 * @return True when a directory sits at path.
 */
b8 sdl_dir_exists(const char* path) {
    SDL_PathInfo info;
    return SDL_GetPathInfo(path, &info) && info.type == SDL_PATHTYPE_DIRECTORY ? TRUE : FALSE;
}

/**
 * @brief gs_platform_read_dir via SDL.
 * @param path The directory to read.
 * @return The names in it, each an owned string; empty on failure. The caller
 * frees each name and then the list.
 */
mg_str_list sdl_read_dir(const char* path) {
    i32 count = 0;
    char** entries = SDL_GlobDirectory(path, NULL, 0, &count);

    mg_str_list names = mg_str_list_new(entries != NULL ? (len)count : 0);
    for (i32 i = 0; entries != NULL && i < count; i++) {
        mg_str name = mg_str_new(mg_view_cstr(entries[i]));
        if (!mg_str_list_push(&names, name)) {
            mg_str_free(&name);
        }
    }

    SDL_free((void*)entries);
    return names;
}

/**
 * @brief gs_platform_read_txt_file via SDL. Characters past an
 * embedded NUL do not survive the trip; a text file has none.
 * @param path The file to read.
 * @return The contents as an owned string, or the NULL-charactered
 * out-of-memory string on failure.
 */
mg_str sdl_read_txt_file(const char* path) {
    /* size_t, not len: SDL_LoadFile writes through a size_t pointer. */
    size_t size = 0;
    char* data = SDL_LoadFile(path, &size);
    if (data == NULL) {
        const mg_str none = {.items = NULL, .count = 0};
        return none;
    }

    const mg_str contents = mg_str_new(mg_view_cstr(data));
    SDL_free(data);
    return contents;
}

/**
 * @brief gs_platform_read_binary_file via SDL.
 * @param path The file to read.
 * @return The contents as an owned byte stream; empty on failure.
 */
mg_byte_list sdl_read_binary_file(const char* path) {
    size_t size = 0;
    void* data = SDL_LoadFile(path, &size);

    mg_byte_list bytes = mg_byte_list_new(data != NULL && size > 0 ? (len)size : 0);
    if (data != NULL && bytes.items != NULL) {
        mg_buffer_push_many(&bytes, data, (len)size);
    }

    SDL_free(data);
    return bytes;
}

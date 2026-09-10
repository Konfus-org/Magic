/**
 * @file file.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief File operations. Implements magic/files/file.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/files/file.h"
#include "magic/platform/slots.h"

/* Thin wrappers over the platform slots: the slot is the implementation, and
 * its default answers when no gem fills it, so nothing here checks anything. */

mg_str mg_read_txt_file(const char* path) {
    return gs_platform_read_txt_file(path);
}

mg_byte_list mg_read_bin_file(const char* path) {
    return gs_platform_read_binary_file(path);
}

b8 mg_create_file(const char* path) {
    /* NOT IMPLEMENTED: no platform slot creates files yet. */
    (void)path;
    return FALSE;
}

b8 mg_copy_file(const char* src_path, const char* dest_path) {
    return gs_platform_copy_file(src_path, dest_path);
}

b8 mg_delete_file(const char* path) {
    return gs_platform_delete_file(path);
}

b8 mg_file_exists(const char* path) {
    return gs_platform_file_exists(path);
}

/**
 * @file dirs.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Directory operations. Implements magic/files/dirs.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/files/dirs.h"
#include "magic/debug/logging.h"
#include "magic/platform/slots.h"

/* Thin wrappers over the platform slots: the slot is the implementation, and
 * its default answers when no gem fills it, so nothing here checks anything.
 * The filter is this layer's own, applied over what the slot hands back. */

mg_str_list mg_read_dir(const char* path, const char* filter) {
    mg_str_list names = gs_platform_read_dir(path);
    if (filter == NULL || *filter == '\0') {
        return names;
    }

    /* Walked back to front, so the swap remove never moves an entry the walk
     * has yet to judge. Dropped names are owned strings, freed as they go. */
    const mg_str_view pattern = mg_view_cstr(filter);
    for (len i = names.count; i > 0; i--) {
        mg_str* name = mg_str_list_at(&names, i - 1);
        if (!mg_str_contains(mg_view_str(*name), pattern)) {
            mg_str_free(name);
            mg_buffer_remove(&names, i - 1);
        }
    }
    return names;
}

mg_str_list mg_read_dir_recursive(const char* path, const char* filter) {
    /* NOT IMPLEMENTED: walking into subdirectories needs the path joining
     * that is not written yet; the top level is what there is. */
    MG_WARN(
        "mg_read_dir_recursive('%s'): recursion is not implemented yet; "
        "returning the top level only.",
        path);
    return mg_read_dir(path, filter);
}

b8 mg_create_dir(const char* path) {
    /* NOT IMPLEMENTED: no platform slot creates directories yet. */
    (void)path;
    return FALSE;
}

b8 mg_delete_dir(const char* path) {
    /* NOT IMPLEMENTED: no platform slot deletes directories yet. */
    (void)path;
    return FALSE;
}

b8 mg_dir_exists(const char* path) {
    return gs_platform_dir_exists(path);
}

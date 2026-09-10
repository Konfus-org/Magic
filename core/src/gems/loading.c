/**
 * @file loading.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Loading, binding and unloading gems. Implements magic/gems/loading.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/gems/loading.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/gems/slots.h"
#include "magic/gems/state.h"
#include "magic/memory/alloc.h"
#include <stdio.h>
#include <string.h>

#if defined(MG_PLATFORM_WINDOWS)
    #include <windows.h>
#elif defined(MG_PLATFORM_IOS) || defined(MG_PLATFORM_LINUX))
    #include <dirent.h>
#else
    #error Platform not supported!
#endif

/* The platform's shared library extension: what marks a file in the gem
 * folder as a gem candidate. The hot-reload copies end in .tmp, so a scan for
 * this suffix never picks one up. */
#if defined(MG_PLATFORM_WINDOWS)
    #define MG_SHARED_LIB_EXT_ ".dll"
#elif defined(MG_PLATFORM_IOS)
    #define MG_SHARED_LIB_EXT_ ".dylib"
#elif defined(MG_PLATFORM_LINUX)
    #define MG_SHARED_LIB_EXT_ ".so"
#else
    #error Platform not supported!
#endif

#if defined(MG_HOT_RELOAD_GEMS)

/* Copies are numbered rather than reusing one name: the delete on unload can
 * fail (a debugger or virus scanner holding the file) and must not stop the
 * next load. The counter spans the process like the temp files it numbers,
 * so it is this module's own, not the app's. */
static u32 g_gem_generation = 0;

/** @brief Builds the path of the next throwaway copy of the gem. Caller frees. */
static char* mg_gem_copy_path(const char* path) {
    const int needed = snprintf(NULL, 0, "%s.%u.tmp", path, g_gem_generation);
    if (needed < 0) {
        return NULL;
    }

    const u64 size = (u64)needed + 1;
    char* copy = mg_alloc_buffer(char, size, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (copy == NULL) {
        return NULL;
    }

    snprintf(copy, size, "%s.%u.tmp", path, g_gem_generation);
    g_gem_generation++;
    return copy;
}

#endif // MG_HOT_RELOAD_GEMS

/**
 * @brief Points the engine's slots at what this gem provides.
 * Last loaded wins, with a warning on taking over another provider's slot; a
 * slot still holding its default is empty, not another provider's.
 */
static void mg_bind_gem(const mg_gem* gem) {
    const mg_gem_slot_table slots = mg_gem_slots();

    for (len i = 0; i < slots.count; i++) {
        const mg_gem_slot* slot = &slots.items[i];
        const mg_lib_fn provided = mg_find_symbol(&gem->lib, slot->name);
        if (provided == NULL) {
            continue;
        }

        if (*slot->ptr_address != NULL && *slot->ptr_address != slot->default_fn
            && *slot->ptr_address != provided) {
            MG_WARN(
                "Gem '%.*s' is taking over '%s' from another provider.",
                (int)gem->desc.name.count,
                (const char*)gem->desc.name.items,
                slot->name);
        }
        *slot->ptr_address = provided;
    }

    /* The inverse walk: a published name no slot answers to is a forgotten
     * register line in a family's slots.c or a typo in the gem's fill table,
     * and either would otherwise fail silently. */
    for (len i = 0; i < gem->lib.symbols.count; i++) {
        const char* published = gem->lib.symbols.items[i].name;
        b8 matched = FALSE;
        for (len j = 0; j < slots.count && !matched; j++) {
            matched = strcmp(slots.items[j].name, published) == 0;
        }
        if (!matched) {
            MG_WARN(
                "Gem '%.*s' fills '%s', which no registered slot answers to.",
                (int)gem->desc.name.count,
                (const char*)gem->desc.name.items,
                published);
        }
    }
}

/**
 * @brief Puts the slots this gem filled back to their defaults, before its
 * image goes away.
 */
static void mg_unbind_gem(const mg_gem* gem) {
    const mg_gem_slot_table slots = mg_gem_slots();

    for (len i = 0; i < slots.count; i++) {
        const mg_gem_slot* slot = &slots.items[i];
        if (*slot->ptr_address != NULL
            && mg_find_symbol(&gem->lib, slot->name) == *slot->ptr_address) {
            *slot->ptr_address = slot->default_fn;
        }
    }
}

/**
 * @brief Opens the library behind the gem, from a throwaway copy when hot
 * reload is compiled in.
 */
static b8 mg_open_gem_lib(mg_gem* gem, const char* path) {
#if defined(MG_HOT_RELOAD_GEMS)

    char* copy = mg_gem_copy_path(path);
    if (copy == NULL) {
        MG_ERROR("Out of memory naming a copy of gem '%s'.", path);
        return FALSE;
    }

    const b8 opened = mg_copy_shared_lib(path, copy) && mg_open_shared_lib(&gem->lib, copy);
    if (!opened) {
        remove(copy);
    }

    // mg_open_shared_lib kept its own copy of the path; unload deletes that one.
    mg_free(copy, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    return opened;

#else

    return mg_open_shared_lib(&gem->lib, path);

#endif
}

/**
 * @brief Closes the image behind the gem and deletes the throwaway copy it was
 * opened from. Leaves the gem itself alone, which is what a reload needs.
 */
static void mg_close_gem_lib(mg_gem* gem) {
#if defined(MG_HOT_RELOAD_GEMS)
    /* Taken off the struct before the close frees it; deleted after, because
     * the file stays locked until the image is unmapped. */
    char* copy = gem->lib.path;
    gem->lib.path = NULL;

    mg_close_shared_lib(&gem->lib);

    if (copy != NULL) {
        remove(copy);
        mg_free(copy, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    }
#else
    mg_close_shared_lib(&gem->lib);
#endif
}

/**
 * @brief Reads the identity off an already-open image and binds what it provides.
 * @return FALSE with the library still open; the caller decides whether that
 * means freeing a new gem or keeping an old one.
 */
static b8 mg_attach_gem(mg_gem* gem, const char* path) {
    // Cast through a named value; casting the call itself trips -Wbad-function-cast.
    const mg_lib_fn describe_export = mg_find_export(&gem->lib, MG_GEM_ENTRY_POINT);
    const mg_gem_describe_fn describe = (mg_gem_describe_fn)describe_export;
    const mg_gem_desc* desc = describe != NULL ? describe() : NULL;
    if (desc == NULL) {
        MG_ERROR("'%s' is not a gem: no %s export.", path, MG_GEM_ENTRY_POINT);
        return FALSE;
    }

    /* Checked before any other field is read: a different ABI is a different
     * layout, and every field sits at an offset that agreement decides. */
    if (desc->abi_version != MG_GEM_ABI_VERSION) {
        MG_ERROR(
            "Gem '%s' was built against gem ABI %u, this engine speaks %u.",
            path,
            desc->abi_version,
            MG_GEM_ABI_VERSION);
        return FALSE;
    }
    gem->desc = *desc;

    mg_bind_gem(gem);
    return TRUE;
}

/**
 * @brief Forgets the image a gem was describing. The desc's strings and hooks
 * point into the library, so zeroing them is what stops a failed reopen from
 * calling into an unmapped page.
 */
static void mg_detach_gem(mg_gem* gem) {
    const mg_gem_desc no_desc = {0};
    gem->desc = no_desc;
}

/** @brief Builds folder/file with the engine's allocator. Caller frees. */
static char* mg_gem_new_file_path(const char* folder, const char* file) {
    const int needed = snprintf(NULL, 0, "%s/%s", folder, file);
    if (needed < 0) {
        return NULL;
    }

    const u64 size = (u64)needed + 1;
    char* path = mg_alloc_buffer(char, size, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (path == NULL) {
        return NULL;
    }

    snprintf(path, size, "%s/%s", folder, file);
    return path;
}

/**
 * @brief Loads the gem file named file inside folder into owner and gems.
 * A failed load leaves the list as it was; the loader already said why.
 */
static void mg_load_gem_into(
    mg_gem_list* gems,
    const char* folder,
    const char* file,
    mg_app* owner) {
    char* path = mg_gem_new_file_path(folder, file);
    if (path == NULL) {
        return;
    }

    mg_gem* gem = mg_load_gem(path, owner);
    mg_free(path, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (gem != NULL && !mg_gem_list_push(gems, gem)) {
        mg_unload_gem(gem);
    }
}

mg_gem* mg_load_gem(const char* path, mg_app* owner) {
    MG_ASSERT_RETURN_VAL_IF(path != NULL, NULL, "path is null");

    mg_gem* gem = mg_alloc_one(mg_gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (gem == NULL) {
        MG_ERROR("Out of memory loading gem '%s'.", path);
        return NULL;
    }

    gem->owner = owner;

    // Copied, not borrowed: this path has to outlive every reload.
    gem->path = mg_str_new(mg_view_cstr(path));
    if (gem->path.items == NULL) {
        MG_ERROR("Out of memory keeping the path of gem '%s'.", path);
        mg_free(gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
        return NULL;
    }

    if (!mg_open_gem_lib(gem, path)) {
        mg_str_free(&gem->path);
        mg_free(gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
        return NULL;
    }

    /* Not mg_unload_gem on this failure path: that fires on_unload, and a
     * gem whose identity was never read has no hooks to fire. */
    if (!mg_attach_gem(gem, path)) {
        mg_close_gem_lib(gem);
        mg_str_free(&gem->path);
        mg_free(gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
        return NULL;
    }

    if (gem->desc.hooks.on_load != NULL) {
        gem->desc.hooks.on_load(gem->owner);
    }

    MG_INFO(
        "Loaded gem '%.*s' %.*s by %.*s.",
        (int)gem->desc.name.count,
        (const char*)gem->desc.name.items,
        (int)gem->desc.version.count,
        (const char*)gem->desc.version.items,
        (int)gem->desc.author.count,
        (const char*)gem->desc.author.items);
    return gem;
}

void mg_unload_gem(mg_gem* gem) {
    MG_ASSERT_RETURN_IF(gem != NULL, "Tried to unload a null gem");
    MG_INFO(
        "Unloading gem '%.*s' %.*s by %.*s.",
        (int)gem->desc.name.count,
        (const char*)gem->desc.name.items,
        (int)gem->desc.version.count,
        (const char*)gem->desc.version.items,
        (int)gem->desc.author.count,
        (const char*)gem->desc.author.items);

    // First, while the image is still mapped and its slots are still bound.
    if (gem->desc.hooks.on_unload != NULL) {
        gem->desc.hooks.on_unload(gem->owner);
    }

    mg_unbind_gem(gem);
    mg_close_gem_lib(gem);
    mg_str_free(&gem->path);
    mg_free(gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
}

void mg_reload_gem(mg_gem* gem) {
    MG_ASSERT_RETURN_IF(gem != NULL, "Tried to reload a null gem");

    /* Read before the close: gem->path is owned and survives the image, unlike
     * gem->desc.name, which is about to be unmapped. */
    const char* path = mg_str_to_cstr(gem->path);
    if (path == NULL) {
        MG_ERROR("Gem cannot be reloaded: it has no path to reload from.");
        return;
    }

    /* What the old image wants the new one to know: it serializes into this
     * before the close, the new image reads it back after the open. Owned here,
     * because the hooks that touch it belong to two different images. */
    mg_byte_list state = mg_byte_list_new(0);

    // The last thing the old image is asked to do, while it can still be asked.
    if (gem->desc.hooks.on_reload != NULL) {
        gem->desc.hooks.on_reload(gem->owner, state);
    }

    mg_unbind_gem(gem);
    mg_close_gem_lib(gem);
    mg_detach_gem(gem);

    if (!mg_open_gem_lib(gem, path)) {
        mg_byte_list_free(&state);
        MG_ERROR(
            "Gem '%s' did not reload and is now unloaded. Its slots are "
            "back to their defaults until a later reload finds the file.",
            path);
        return;
    }

    if (!mg_attach_gem(gem, path)) {
        mg_close_gem_lib(gem);
        mg_detach_gem(gem);
        mg_byte_list_free(&state);
        MG_ERROR("Gem '%s' reopened but did not attach, and is now unloaded.", path);
        return;
    }

    // On the new image, and only once it is bound, so the hook finds its slots filled.
    if (gem->desc.hooks.on_reloaded != NULL) {
        gem->desc.hooks.on_reloaded(gem->owner, state);
    }
    mg_byte_list_free(&state);

    MG_INFO(
        "Reloaded gem '%.*s' %.*s by %.*s.",
        (int)gem->desc.name.count,
        (const char*)gem->desc.name.items,
        (int)gem->desc.version.count,
        (const char*)gem->desc.version.items,
        (int)gem->desc.author.count,
        (const char*)gem->desc.author.items);
}

mg_gem_list mg_load_gems(const char* path, mg_app* owner) {
    mg_gem_list gems = mg_gem_list_construct();

    // An app with no gem folder is a legitimate configuration, not an error.
    if (path == NULL || *path == '\0') {
        return gems;
    }

    /* The engine's own scan, never the read_dir slot: gems are what fill the
     * slots, so the loader cannot ask a gem to find gems. One flat pass over
     * the given folder; a gem is any file ending in the platform's library
     * extension, and subfolders are not entered. */
#if defined(MG_PLATFORM_WINDOWS)
    char* pattern = mg_gem_new_file_path(path, "*" MG_SHARED_LIB_EXT_);
    if (pattern == NULL) {
        return gems;
    }

    WIN32_FIND_DATAA found;
    const HANDLE find = FindFirstFileA(pattern, &found);
    mg_free(pattern, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (find == INVALID_HANDLE_VALUE) {
        MG_WARN("No gems found in '%s'.", path);
        return gems;
    }

    do {
        // A folder named like a library is not a gem.
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            continue;
        }
        mg_load_gem_into(&gems, path, found.cFileName, owner);
    } while (FindNextFileA(find, &found) != 0);
    FindClose(find);
#elif defined(MG_PLATFORM_IOS) || defined(MG_PLATFORM_LINUX)
    DIR* dir = opendir(path);
    if (dir == NULL) {
        MG_WARN("No gems found in '%s': the folder did not open.", path);
        return gems;
    }

    const struct dirent* entry;
    while ((entry = readdir(dir)) != NULL) {
        /* Kept only when the name ends in the extension; that also skips the
         * . and .. entries, which are too short to. */
        const char* name = entry->d_name;
        const size_t length = strlen(name);
        const size_t ext_length = sizeof(MG_SHARED_LIB_EXT_) - 1;
        if (length <= ext_length || strcmp(name + (length - ext_length), MG_SHARED_LIB_EXT_) != 0) {
            continue;
        }
        mg_load_gem_into(&gems, path, name, owner);
    }
    closedir(dir);
#endif

    return gems;
}

void mg_unload_gems(mg_gem_list* gems) {
    MG_ASSERT_RETURN_IF(gems != NULL, "gems list is null");

    /* The list holds plain pointers with no destructor behind them, so the
     * unload is spelled out: every gem still in the list, then the storage. */
    mg_gem_ptr* slot;
    mg_where(slot, gems, *slot != NULL) {
        mg_unload_gem(*slot);
    }
    mg_gem_list_free(gems);
}

void mg_reload_gems(mg_gem_list* gems) {
    MG_ASSERT_RETURN_IF(gems != NULL, "gems list is null");

    // Skips the empty slot a failed load may have left behind.
    mg_gem_ptr* slot;
    mg_where(slot, gems, *slot != NULL) {
        mg_reload_gem(*slot);
    }
}

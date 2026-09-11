/**
 * @file loading.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Loading, binding and unloading gems. Implements magic/gems/loading.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/gems/loading.h"
#include "magic/collections/string.h"
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

/* A name as the MG_* format arguments want it: a view carries no terminator,
 * so every mention of one spells out the precision and the cast. Pairs with a
 * %.*s in the format. */
#define MG_VIEW_ARGS_(view) (int)(view).count, (const char*)(view).items

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
                MG_VIEW_ARGS_(gem->desc.name),
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
                MG_VIEW_ARGS_(gem->desc.name),
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
 * @brief Reads the identity off an already-open image onto the gem: who it
 * is, what it needs and what it does. Nothing is bound and no hook fires, so
 * the dependencies a set is ordered by are readable before any gem is stood
 * up.
 * @return FALSE with the library still open; the caller decides whether that
 * means freeing a new gem or keeping an old one.
 */
static b8 mg_read_gem_desc(mg_gem* gem, const char* path) {
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

/**
 * @brief Gives back everything a gem holds without firing a hook: its image,
 * its path and itself. The teardown for a gem whose on_load never ran, so it
 * has no on_unload owing; mg_unload_gem is the one for a gem that is up.
 */
static void mg_free_gem(mg_gem* gem) {
    mg_close_gem_lib(gem);
    mg_str_free(&gem->path);
    mg_free(gem, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
}

/**
 * @brief Opens the gem at path and reads its identity, stopping short of
 * standing it up: nothing is bound and on_load has not fired. The half of a
 * load that answers who this is and what it needs.
 * @return The gem, unbound, or NULL on failure.
 */
static mg_gem* mg_open_gem(const char* path, mg_app* owner) {
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

    // mg_free_gem and not mg_unload_gem: no hook has fired, so none is owed.
    if (!mg_read_gem_desc(gem, path)) {
        mg_free_gem(gem);
        return NULL;
    }

    return gem;
}

/**
 * @brief Stands an opened gem up: fills the slots it provides, then fires its
 * on_load. The other half of a load, split off so a set can be ordered
 * between the two -- a gem is only ever activated once everything it named
 * with MG_GEM_REQUIRES already has been.
 */
static void mg_activate_gem(mg_gem* gem) {
    mg_bind_gem(gem);

    if (gem->desc.hooks.on_load != NULL) {
        gem->desc.hooks.on_load(gem->owner);
    }

    MG_INFO(
        "Loaded gem '%.*s' %.*s by %.*s.",
        MG_VIEW_ARGS_(gem->desc.name),
        MG_VIEW_ARGS_(gem->desc.version),
        MG_VIEW_ARGS_(gem->desc.author));
}

/**
 * @brief The gem in gems answering to name, or NULL when none does. Matching
 * is exact: a dependency names the gem the way that gem's own MG_PROVIDE_GEM
 * does.
 */
static const mg_gem* mg_gem_named(const mg_gem_list* gems, const mg_str_view name) {
    mg_gem_list_iter it = mg_gem_list_it(gems);
    const mg_gem_ptr* slot;
    while ((slot = mg_gem_list_iter_next(&it)) != NULL) {
        if (*slot != NULL && mg_str_eq((*slot)->desc.name, name)) {
            return *slot;
        }
    }
    return NULL;
}

/** @brief Whether every gem this one requires is already standing in loaded. */
static b8 mg_gem_deps_met(const mg_gem* gem, const mg_gem_list* loaded) {
    for (len i = 0; i < gem->desc.dependency_count; i++) {
        if (mg_gem_named(loaded, gem->desc.dependencies[i]) == NULL) {
            return FALSE;
        }
    }
    return TRUE;
}

/**
 * @brief Says why a gem could not be stood up, one line per dependency it is
 * still waiting on. A name another gem still in the pool answers to is a
 * cycle -- that one is waiting too, or it would have gone first; a name the
 * pool has none of is a gem the folder simply does not hold.
 * @param loaded The gems already standing, whose names are the met ones.
 * @param stuck The gems left in the pool, this one among them.
 */
static void mg_report_unmet_deps(
    const mg_gem* gem,
    const mg_gem_list* loaded,
    const mg_gem_list* stuck) {
    for (len i = 0; i < gem->desc.dependency_count; i++) {
        const mg_str_view needed = gem->desc.dependencies[i];
        if (mg_gem_named(loaded, needed) != NULL) {
            continue;
        }

        MG_ERROR(
            "Gem '%.*s' is not loaded: it requires '%.*s', which %s.",
            MG_VIEW_ARGS_(gem->desc.name),
            MG_VIEW_ARGS_(needed),
            mg_gem_named(stuck, needed) != NULL ? "is itself waiting on a dependency cycle"
                                               : "no gem in the folder provides");
    }
}

/**
 * @brief Stands the pooled gems up in dependency order, moving each into
 * loaded as it goes.
 * Passes over the pool until one takes nothing: each pass activates every gem
 * whose dependencies are already standing, so a chain comes up a link per
 * pass. A pass that takes nothing with gems still in the pool has found the
 * two cases nothing can fix -- a dependency the folder has none of, and a
 * cycle -- and those are reported and freed rather than stood up half way.
 * Quadratic in the gem count by construction, over a list one folder scan
 * bounds; the alternative is a graph built to save microseconds once at load.
 * @param pool The opened gems; emptied of every gem this takes or refuses.
 * @param loaded Receives each gem as it is stood up, in that order.
 */
static void mg_activate_in_dependency_order(mg_gem_list* pool, mg_gem_list* loaded) {
    b8 took_one = TRUE;
    while (took_one) {
        took_one = FALSE;

        mg_gem_ptr* slot;
        mg_where(slot, pool, *slot != NULL) {
            if (!mg_gem_deps_met(*slot, loaded)) {
                continue;
            }

            /* Out of the pool before it is stood up: on_load is the gem's
             * first chance to look around, and what it finds should not
             * include itself still waiting to be loaded. */
            mg_gem* gem = *slot;
            *slot = NULL;
            took_one = TRUE;

            mg_activate_gem(gem);
            if (!mg_gem_list_push(loaded, gem)) {
                MG_ERROR(
                    "Out of memory keeping gem '%.*s'; unloading it again.",
                    MG_VIEW_ARGS_(gem->desc.name));
                mg_unload_gem(gem);
            }
        }
    }

    // Whatever the passes could not take, and why.
    mg_gem_ptr* left;
    mg_where(left, pool, *left != NULL) {
        mg_report_unmet_deps(*left, loaded, pool);
        // Never stood up, so it has no on_unload owing.
        mg_free_gem(*left);
        *left = NULL;
    }
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
 * @brief Notes the gem file named file inside folder as one to open. The scan
 * only collects: with dependencies to order by, nothing can be stood up until
 * every candidate has been seen.
 */
static void mg_collect_gem_path(mg_str_list* paths, const char* folder, const char* file) {
    char* path = mg_gem_new_file_path(folder, file);
    if (path == NULL) {
        return;
    }

    mg_str kept = mg_str_new(mg_view_cstr(path));
    mg_free(path, MG_MEMORY_TAG_DYNAMIC_LIBRARY);

    if (kept.items == NULL) {
        MG_ERROR("Out of memory noting the path of gem '%s'.", file);
        return;
    }
    if (!mg_str_list_push(paths, kept)) {
        MG_ERROR("Out of memory collecting the path of gem '%s'.", file);
        mg_str_free(&kept);
    }
}

/** @brief Frees every path a scan collected, and the list holding them. */
static void mg_free_gem_paths(mg_str_list* paths) {
    mg_str* path;
    mg_foreach(path, paths) {
        mg_str_free(path);
    }
    mg_str_list_free(paths);
}

/**
 * @brief The gem files in folder, one flat pass.
 * The engine's own scan, never the read_dir slot: gems are what fill the
 * slots, so the loader cannot ask a gem to find gems. A gem is any file
 * ending in the platform's library extension, and subfolders are not entered.
 * @return The paths, owned; hand them to mg_free_gem_paths.
 */
static mg_str_list mg_scan_for_gems(const char* folder) {
    mg_str_list paths = mg_str_list_construct();

#if defined(MG_PLATFORM_WINDOWS)
    char* pattern = mg_gem_new_file_path(folder, "*" MG_SHARED_LIB_EXT_);
    if (pattern == NULL) {
        return paths;
    }

    WIN32_FIND_DATAA found;
    const HANDLE find = FindFirstFileA(pattern, &found);
    mg_free(pattern, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (find == INVALID_HANDLE_VALUE) {
        MG_WARN("No gems found in '%s'.", folder);
        return paths;
    }

    do {
        // A folder named like a library is not a gem.
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            continue;
        }
        mg_collect_gem_path(&paths, folder, found.cFileName);
    } while (FindNextFileA(find, &found) != 0);
    FindClose(find);
#elif defined(MG_PLATFORM_IOS) || defined(MG_PLATFORM_LINUX)
    DIR* dir = opendir(folder);
    if (dir == NULL) {
        MG_WARN("No gems found in '%s': the folder did not open.", folder);
        return paths;
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
        mg_collect_gem_path(&paths, folder, name);
    }
    closedir(dir);
#endif

    return paths;
}

mg_gem* mg_load_gem(const char* path, mg_app* owner) {
    MG_ASSERT_RETURN_VAL_IF(path != NULL, NULL, "path is null");

    mg_gem* gem = mg_open_gem(path, owner);
    if (gem == NULL) {
        return NULL;
    }

    /* Straight through: one gem loaded by name has no set to be ordered
     * against, so whatever it requires is the caller's to have loaded first. */
    mg_activate_gem(gem);
    return gem;
}

void mg_unload_gem(mg_gem* gem) {
    MG_ASSERT_RETURN_IF(gem != NULL, "Tried to unload a null gem");
    MG_INFO(
        "Unloading gem '%.*s' %.*s by %.*s.",
        MG_VIEW_ARGS_(gem->desc.name),
        MG_VIEW_ARGS_(gem->desc.version),
        MG_VIEW_ARGS_(gem->desc.author));

    // First, while the image is still mapped and its slots are still bound.
    if (gem->desc.hooks.on_unload != NULL) {
        gem->desc.hooks.on_unload(gem->owner);
    }

    mg_unbind_gem(gem);
    mg_free_gem(gem);
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

    if (!mg_read_gem_desc(gem, path)) {
        mg_close_gem_lib(gem);
        mg_detach_gem(gem);
        mg_byte_list_free(&state);
        MG_ERROR("Gem '%s' reopened but did not attach, and is now unloaded.", path);
        return;
    }

    /* Bound, but no on_load: a reload is the same gem carrying on, and the
     * gems it stands on never went anywhere. mg_reload_gems keeps load order,
     * so by here each of those already has its own new image. */
    mg_bind_gem(gem);

    // Only once it is bound, so the hook finds its slots filled.
    if (gem->desc.hooks.on_reloaded != NULL) {
        gem->desc.hooks.on_reloaded(gem->owner, state);
    }
    mg_byte_list_free(&state);

    MG_INFO(
        "Reloaded gem '%.*s' %.*s by %.*s.",
        MG_VIEW_ARGS_(gem->desc.name),
        MG_VIEW_ARGS_(gem->desc.version),
        MG_VIEW_ARGS_(gem->desc.author));
}

mg_gem_list mg_load_gems(const char* path, mg_app* owner) {
    mg_gem_list gems = mg_gem_list_construct();

    // An app with no gem folder is a legitimate configuration, not an error.
    if (path == NULL || *path == '\0') {
        return gems;
    }

    mg_str_list paths = mg_scan_for_gems(path);

    /* Every candidate opened before any is stood up: what a gem requires is
     * in its desc, and a desc is only readable once its image is. The pool
     * holds them open but unbound, hooks unfired, until the order is known. */
    mg_gem_list pool = mg_gem_list_construct();
    mg_str* file;
    mg_foreach(file, &paths) {
        mg_gem* gem = mg_open_gem(mg_str_to_cstr(*file), owner);
        if (gem != NULL && !mg_gem_list_push(&pool, gem)) {
            MG_ERROR("Out of memory pooling gem '%s'.", mg_str_to_cstr(*file));
            mg_free_gem(gem);
        }
    }
    mg_free_gem_paths(&paths);

    mg_activate_in_dependency_order(&pool, &gems);
    mg_gem_list_free(&pool);

    return gems;
}

void mg_unload_gems(mg_gem_list* gems) {
    MG_ASSERT_RETURN_IF(gems != NULL, "gems list is null");

    /* Back to front, the mirror of the order they were stood up in, so a gem
     * is torn down while every gem it depends on is still up and its
     * on_unload may still call into one. The list holds plain pointers with
     * no destructor behind them, so the unload is spelled out; an index walk
     * rather than mg_foreach, which only runs forwards, and counting down to
     * one rather than to zero, because len is unsigned. */
    for (len i = gems->count; i > 0; i--) {
        mg_gem_ptr* slot = mg_gem_list_at(gems, (ind)(i - 1));
        if (slot != NULL && *slot != NULL) {
            mg_unload_gem(*slot);
        }
    }
    mg_gem_list_free(gems);
}

void mg_reload_gems(mg_gem_list* gems) {
    MG_ASSERT_RETURN_IF(gems != NULL, "gems list is null");

    /* Forwards, which is load order: a gem's dependencies get their new
     * images before it gets its own. Skips the empty slot a failed load may
     * have left behind. */
    mg_gem_ptr* slot;
    mg_where(slot, gems, *slot != NULL) {
        mg_reload_gem(*slot);
    }
}

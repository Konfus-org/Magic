/**
 * @file shared_lib.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Loading libraries at runtime. Implements magic/platform/shared_lib.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/platform/shared_lib.h"
#include "magic/collections/string.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/memory/alloc.h"
#include "magic/memory/copy.h"
#include <stdio.h>
#include <string.h>

#if defined(MG_PLATFORM_WINDOWS)
    #include <windows.h>
#else
    #include <dlfcn.h>
#endif

/** @brief Opens the library file with the OS loader. Returns NULL on failure. */
static void* mg_os_open_lib(const char* path) {
#if defined(MG_PLATFORM_WINDOWS)
    return (void*)LoadLibraryA(path);
#else
    /* RTLD_NOW so a missing dependency fails the load here, loudly, rather
     * than at the first call into it. RTLD_LOCAL keeps one gem's symbols from
     * shadowing another's. */
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
}

/** @brief Closes what mg_os_open_lib opened. */
static void mg_os_close_lib(void* handle) {
#if defined(MG_PLATFORM_WINDOWS)
    FreeLibrary((HMODULE)handle);
#else
    dlclose(handle);
#endif
}

/** @brief Looks up an OS export on the handle. Returns NULL if absent. */
static mg_lib_fn mg_os_find_export(void* handle, const char* name) {
#if defined(MG_PLATFORM_WINDOWS)
    /* Through a named value and not around the call itself, which is the shape
     * -Wbad-function-cast rejects. FARPROC is already a function pointer, so
     * the cast is the kind ISO C allows. */
    const FARPROC address = GetProcAddress((HMODULE)handle, name);
    return (mg_lib_fn)address;
#else
    /* dlsym returns void*, and ISO C has no conversion from an object pointer
     * to a function pointer -- POSIX guarantees the two share a size on every
     * platform with dlsym, so the bytes are moved rather than cast. */
    void* address = dlsym(handle, name);
    mg_lib_fn fn = NULL;
    if (address != NULL) {
        memcpy(&fn, &address, sizeof(fn));
    }
    return fn;
#endif
}

b8 mg_open_shared_lib(mg_shared_lib* lib, const char* path) {
    MG_ASSERT_RETURN_VAL_IF(lib != NULL, FALSE, "lib is null");
    MG_ASSERT_RETURN_VAL_IF(path != NULL, FALSE, "path is null");

    const mg_shared_lib empty = {NULL, NULL, {NULL, 0}};
    *lib = empty;

    void* handle = mg_os_open_lib(path);
    if (handle == NULL) {
        MG_ERROR("Could not open shared library '%s'.", path);
        return FALSE;
    }
    lib->handle = handle;

    /* Copied, not borrowed: the caller's path is often a buffer it is about to
     * reuse, and this one has to survive until the close. */
    const len length = mg_cstr_length(path);
    char* owned_path = mg_alloc_buffer(char, length + 1, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (owned_path == NULL) {
        MG_ERROR("Out of memory keeping the path of '%s'.", path);
        mg_os_close_lib(handle);
        lib->handle = NULL;
        return FALSE;
    }
    mg_mem_copy(owned_path, path, length + 1);
    lib->path = owned_path;

    /* The published table, if there is one -- absence is a library that
     * publishes nothing, not an error. Cast through a named value, which is
     * the shape -Wbad-function-cast asks for. */
    const mg_lib_fn table_export = mg_find_export(lib, MG_SYMBOLS_ENTRY_POINT);
    const mg_lib_symbols_fn publish = (mg_lib_symbols_fn)table_export;
    if (publish != NULL) {
        const mg_lib_symbol* begin = NULL;
        const mg_lib_symbol* end = NULL;
        publish(&begin, &end);
        if (begin != NULL && end > begin) {
            lib->symbols.items = begin;
            lib->symbols.count = (len)(end - begin);
        }
    }

    return TRUE;
}

void mg_close_shared_lib(mg_shared_lib* lib) {
    MG_ASSERT_RETURN_IF(lib != NULL, "lib is null");

    if (lib->handle != NULL) {
        mg_os_close_lib(lib->handle);
    }
    mg_free(lib->path, MG_MEMORY_TAG_DYNAMIC_LIBRARY);

    /* Zeroed whole: the symbol table pointed into the image just unmapped, so
     * leaving it readable would be leaving it dangling. */
    const mg_shared_lib empty = {NULL, NULL, {NULL, 0}};
    *lib = empty;
}

mg_shared_lib* mg_load_shared_lib(const char* path) {
    MG_ASSERT_RETURN_VAL_IF(path != NULL, NULL, "path is null");

    mg_shared_lib* lib = mg_alloc_one(mg_shared_lib, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
    if (lib == NULL) {
        MG_ERROR("Out of memory loading '%s'.", path);
        return NULL;
    }

    if (!mg_open_shared_lib(lib, path)) {
        mg_free(lib, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
        return NULL;
    }
    return lib;
}

void mg_unload_shared_lib(mg_shared_lib* lib) {
    MG_ASSERT_RETURN_IF(lib != NULL, "lib is null");

    mg_close_shared_lib(lib);
    mg_free(lib, MG_MEMORY_TAG_DYNAMIC_LIBRARY);
}

mg_lib_fn mg_find_symbol(const mg_shared_lib* lib, const char* name) {
    MG_ASSERT_RETURN_VAL_IF(lib != NULL, NULL, "lib is null");
    MG_ASSERT_RETURN_VAL_IF(name != NULL, NULL, "name is null");

    for (len i = 0; i < lib->symbols.count; i++) {
        const mg_lib_symbol* symbol = &lib->symbols.items[i];
        if (symbol->name != NULL && strcmp(symbol->name, name) == 0) {
            return symbol->func_ptr;
        }
    }
    return NULL;
}

b8 mg_copy_shared_lib(const char* path, const char* copy_path) {
    MG_ASSERT_RETURN_VAL_IF(path != NULL, FALSE, "path is null");
    MG_ASSERT_RETURN_VAL_IF(copy_path != NULL, FALSE, "copy_path is null");

#if defined(MG_PLATFORM_WINDOWS)

    // The last argument is bFailIfExists, and overwriting is the contract here.
    if (CopyFileA(path, copy_path, FALSE) == 0) {
        MG_ERROR("Could not copy '%s' to '%s'.", path, copy_path);
        return FALSE;
    }
    return TRUE;

#elif defined(MG_PLATFORM_LINUX) || defined(MG_PLATFORM_IOS)

    /* Plain buffered stdio: no sendfile, no clonefile, nothing per-OS. A gem
     * is a few megabytes copied once per reload, not a hot path. */
    FILE* source = fopen(path, "rb");
    if (source == NULL) {
        MG_ERROR("Could not open '%s' to copy it.", path);
        return FALSE;
    }

    FILE* destination = fopen(copy_path, "wb");
    if (destination == NULL) {
        MG_ERROR("Could not open '%s' to copy '%s' over it.", copy_path, path);
        fclose(source);
        return FALSE;
    }

    b8 copied = TRUE;
    byte chunk[KILOBYTES(4)];
    size_t bytes_read;
    while ((bytes_read = fread(chunk, 1, sizeof(chunk), source)) > 0) {
        if (fwrite(chunk, 1, bytes_read, destination) != bytes_read) {
            copied = FALSE;
            break;
        }
    }

    if (ferror(source)) {
        copied = FALSE;
    }
    // fclose and not just fwrite decides success: the last buffer flushes here.
    if (fclose(destination) != 0) {
        copied = FALSE;
    }
    fclose(source);

    if (!copied) {
        MG_ERROR("Could not copy '%s' to '%s'.", path, copy_path);
        remove(copy_path);
    }
    return copied;

#else

    #error "Unsupported platform"

#endif
}

mg_lib_fn mg_find_export(const mg_shared_lib* lib, const char* name) {
    MG_ASSERT_RETURN_VAL_IF(lib != NULL, NULL, "lib is null");
    MG_ASSERT_RETURN_VAL_IF(name != NULL, NULL, "name is null");

    if (lib->handle == NULL) {
        return NULL;
    }
    return mg_os_find_export(lib->handle, name);
}

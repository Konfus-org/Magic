/**
 * @file shared_lib.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Loading libraries at runtime, and the symbols they trade.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/primitives.h"

#define MG_NIL_SHARED_LIB NULL

/**
 * @brief The export a library publishes its symbol table through.
 * A library that publishes nothing may leave it out; absence is not an error.
 */
#define MG_SYMBOLS_ENTRY_POINT "mg_lib_symbols"

/**
 * @brief Marks a function as part of a loadable library's public ABI.
 * Gems are built with hidden visibility, so this is the only thing that puts a
 * symbol in the ABI.
 */
#if defined(MG_PLATFORM_WINDOWS)
    #define MG_LIB_EXPORT __declspec(dllexport)
#elif defined(MG_PLATFORM_IOS) || defined(MG_PLATFORM_LINUX)
    #define MG_LIB_EXPORT __attribute__((visibility("default")))
#else
    #error "Magic has no MG_LIB_EXPORT for this platform."
#endif

/**
 * @brief One entry in a library's published table.
 * @param api The declared name to publish under.
 * @param impl The function that implements it. Needs a name of its own, since
 * the header already declares api.
 */
#define MG_PROVIDE_SYMBOL(api, impl) {.name = #api, .func_ptr = (mg_lib_fn) & impl}

/**
 * @brief Publishes a library's symbol table.
 * Goes at file scope in exactly one .c of the library, and may be left out by
 * one that provides nothing.
 * @code
 * MG_PUBLISH_SYMBOLS(
 *     MG_PROVIDE_SYMBOL(gs_platform_setup, win32_setup_platform),
 *     MG_PROVIDE_SYMBOL(gs_platform_cleanup, win32_shutdown_platform)
 * )
 * @endcode
 */
#define MG_PUBLISH_SYMBOLS(...)                                                                 \
    static const mg_lib_symbol __mg_lib_syms[] = {{NULL, NULL}, __VA_ARGS__};                \
    MG_LIB_EXPORT void mg_lib_symbols(                                                       \
        const mg_lib_symbol** out_begin,                                                        \
        const mg_lib_symbol** out_end);                                                         \
    MG_LIB_EXPORT void mg_lib_symbols(                                                       \
        const mg_lib_symbol** out_begin,                                                        \
        const mg_lib_symbol** out_end) {                                                        \
        *out_begin = __mg_lib_syms + 1;                                                         \
        *out_end = __mg_lib_syms + (sizeof(__mg_lib_syms) / sizeof(__mg_lib_syms[0]));    \
    }

/** @brief A function pointer of no particular signature. */
typedef void (*mg_lib_fn)(void);

/** @brief One function a library publishes, and the name it answers to. */
typedef struct mg_lib_symbol {
    const char* name;
    mg_lib_fn func_ptr;
} mg_lib_symbol;

/**
 * @brief The table of symbols a library publishes.
 * Borrowed from the library's own image; never free it.
 */
typedef struct mg_lib_symbol_table {
    const mg_lib_symbol* items;
    len count;
} mg_lib_symbol_table;

/** @brief The function provided by the MG_SYMBOLS_ENTRY_POINT. */
typedef void (
    *mg_lib_symbols_fn)(const mg_lib_symbol** out_begin, const mg_lib_symbol** out_end);

/** @brief A library loaded at runtime, and what it publishes. */
typedef struct mg_shared_lib {
    void* handle;
    char* path;                     /**< Owned copy, freed on close. */
    mg_lib_symbol_table symbols; /**< Borrowed; dangles once the library is unloaded. */
} mg_shared_lib;

/**
 * @brief Loads a library into storage the caller owns. Does not allocate.
 * @param lib Storage to load into.
 * @param path Path of the library file.
 * @return True on success.
 */
MG_EXPORT b8 mg_open_shared_lib(mg_shared_lib* lib, const char* path);

/**
 * @brief Unloads and clears a library opened in place.
 * @param lib The library to close. Not freed; the caller owns its storage.
 */
MG_EXPORT void mg_close_shared_lib(mg_shared_lib* lib);

/**
 * @brief Allocates and loads a standalone library.
 * @param path Path of the library file.
 * @return The new library, or NULL on failure. Free with mg_unload_shared_lib.
 */
MG_EXPORT mg_shared_lib* mg_load_shared_lib(const char* path);

/** @brief Unloads and frees a library that came from mg_load_shared_lib. */
MG_EXPORT void mg_unload_shared_lib(mg_shared_lib* lib);

/**
 * @brief Looks up a function in the library's published symbol table.
 * @param lib The loaded library to search.
 * @param name The name the symbol was published under.
 * @return The function, or NULL if the library does not publish it.
 */
MG_EXPORT mg_lib_fn mg_find_symbol(const mg_shared_lib* lib, const char* name);

/**
 * @brief Copies the library file at path to copy_path.
 * Loading a throwaway copy leaves the original rebuildable while the engine runs.
 * @param path Library file to copy.
 * @param copy_path Destination, overwritten if it exists.
 * @return True on success.
 */
MG_EXPORT b8 mg_copy_shared_lib(const char* path, const char* copy_path);

/**
 * @brief Looks up a symbol the library exports to the OS.
 * Distinct from mg_find_symbol, which searches the published table.
 * @param lib The loaded library to search.
 * @param name The exported symbol name.
 * @return The function, or NULL if the library does not export it.
 */
MG_EXPORT mg_lib_fn mg_find_export(const mg_shared_lib* lib, const char* name);

/**
 * @file export.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief MG_EXPORT, the marker that puts a function in the library's ABI.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/platform/defines.h"

/*
 * Three states, not two.
 *
 * A Windows DLL needs dllexport while the library is being compiled and
 * dllimport at every consumer. COMPILING_MAGIC tells those apart, and
 * core/CMakeLists.txt defines it PRIVATE for exactly that reason.
 *
 * A static library needs neither. dllimport on a statically linked symbol makes
 * the compiler emit an indirect call through an import thunk that no .lib
 * provides, so a static build that skipped this branch would compile and then
 * fail to link. MG_SHARED separates the two, and is only defined when
 * BUILD_SHARED_LIBS is on.
 *
 * ELF and Mach-O would export everything by default, but core/CMakeLists.txt
 * builds with hidden visibility so that MG_EXPORT alone decides the ABI. A
 * shared build there has to put default visibility back per function, the way
 * MG_LIB_EXPORT does for gems -- left empty, libmagic.so exports nothing and
 * every consumer fails to link.
 */
#if defined(MG_PLATFORM_WINDOWS) && defined(MG_SHARED)
    #ifdef COMPILING_MAGIC
        /** @brief Exports a function from the Magic DLL being built. */
        #define MG_EXPORT __declspec(dllexport)
    #else
        /** @brief Imports a function from the Magic DLL being consumed. */
        #define MG_EXPORT __declspec(dllimport)
    #endif
#elif defined(MG_SHARED) && (defined(__GNUC__) || defined(__clang__))
    /** @brief Exports a function from the Magic shared object past hidden visibility. */
    #define MG_EXPORT __attribute__((visibility("default")))
#else
    /** @brief Expands to nothing: a static library needs no marker. */
    #define MG_EXPORT
#endif

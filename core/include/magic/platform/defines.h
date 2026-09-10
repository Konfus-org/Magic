/**
 * @file defines.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The one platform macro every Magic translation unit is compiled with.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once

/* cmake/MagicPlatform.cmake passes one of these on the command line, and that
 * answer wins: only the build knows a cross-compile's target. The fallback
 * covers a consumer compiling without it, such as an IDE indexing a file. */
#if !defined(MG_PLATFORM_WINDOWS) && !defined(MG_PLATFORM_LINUX) &&                           \
    !defined(MG_PLATFORM_IOS)
    #if defined(_WIN32)
        /** @brief Defined when the target is Windows. */
        #define MG_PLATFORM_WINDOWS
    #elif defined(__linux__)
        /** @brief Defined when the target is Linux. */
        #define MG_PLATFORM_LINUX
    #elif defined(__APPLE__)
        /** @brief Defined when the target is iOS or macOS. */
        #define MG_PLATFORM_IOS
    #else
        #error "Magic has no platform macro for this target. Add one next to " \
               "MG_PLATFORM_WINDOWS, MG_PLATFORM_LINUX and MG_PLATFORM_IOS."
    #endif
#endif

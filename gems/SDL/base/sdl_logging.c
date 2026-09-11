/**
 * @file sdl_logging.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Logging: SDL's own log output routed UP into the engine's log, so
 * SDL speaks through the same MG_* dispatch and the same registered loggers
 * as everything else. One direction only -- nothing fills a console slot
 * pointing back at SDL_Log, which would be a message going round forever.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "sdl_base.h"
#include <SDL3/SDL.h>

/**
 * @brief The engine severity an SDL priority means.
 * @param priority The priority SDL tagged the message with.
 * @return The level, INFO for a priority this SDL knows and this map does not.
 */
static mg_log_level sdl_level_from_priority(const SDL_LogPriority priority) {
    switch (priority) {
        case SDL_LOG_PRIORITY_TRACE:
        case SDL_LOG_PRIORITY_VERBOSE:
            return MG_LOG_LEVEL_TRACE;
        case SDL_LOG_PRIORITY_DEBUG:
            return MG_LOG_LEVEL_DEBUG;
        case SDL_LOG_PRIORITY_INFO:
            return MG_LOG_LEVEL_INFO;
        case SDL_LOG_PRIORITY_WARN:
            return MG_LOG_LEVEL_WARN;
        case SDL_LOG_PRIORITY_ERROR:
            return MG_LOG_LEVEL_ERROR;
        case SDL_LOG_PRIORITY_CRITICAL:
            return MG_LOG_LEVEL_FATAL;
        case SDL_LOG_PRIORITY_INVALID:
        case SDL_LOG_PRIORITY_COUNT:
        default:
            return MG_LOG_LEVEL_INFO;
    }
}

/**
 * @brief What SDL's category names, for the call site a log line carries.
 * An engine line carries the file it was written in; an SDL line has no file
 * this side of SDL, so the category stands in its place.
 * @param category The category SDL tagged the message with.
 * @return A literal in this image, so it outlives the call.
 */
static const char* sdl_category_name(const int category) {
    switch (category) {
        case SDL_LOG_CATEGORY_APPLICATION:
            return "SDL/application";
        case SDL_LOG_CATEGORY_ERROR:
            return "SDL/error";
        case SDL_LOG_CATEGORY_SYSTEM:
            return "SDL/system";
        case SDL_LOG_CATEGORY_AUDIO:
            return "SDL/audio";
        case SDL_LOG_CATEGORY_VIDEO:
            return "SDL/video";
        case SDL_LOG_CATEGORY_RENDER:
            return "SDL/render";
        case SDL_LOG_CATEGORY_INPUT:
            return "SDL/input";
        case SDL_LOG_CATEGORY_GPU:
            return "SDL/gpu";
        default:
            return "SDL";
    }
}

/**
 * @brief SDL's log writer as a call into the engine's log.
 * @param userdata Unused; nothing rides along with the callback.
 * @param category The SDL category, which stands in for the call site's file.
 * @param priority The SDL severity, mapped to an engine level.
 * @param message The line itself, already formatted by SDL.
 */
static void SDLCALL sdl_write_to_magic_log(
    void* userdata,
    const int category,
    const SDL_LogPriority priority,
    const char* message) {
    MG_UNUSED(userdata);

    /* "%s" and not message: the line SDL hands over is text, and a stray
     * percent in it is a device name, not a conversion to go reading
     * arguments for. */
    mg_trace(sdl_level_from_priority(priority), sdl_category_name(category), 0, "%s", message);
}

void sdl_route_logging_to_magic(void) {
    SDL_SetLogPriorities(SDL_LOG_PRIORITY_TRACE);
    SDL_SetLogOutputFunction(sdl_write_to_magic_log, NULL);
}

void sdl_restore_logging(void) {
    SDL_SetLogOutputFunction(SDL_GetDefaultLogOutputFunction(), NULL);
}

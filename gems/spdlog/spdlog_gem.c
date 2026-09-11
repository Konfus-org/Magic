/**
 * @file spdlog_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The spdlog gem's identity: the logger it puts on the engine's log,
 * and the lifecycle hooks that put it there and take it away again. The sink
 * behind it is C++ and lives in spdlog_sink.cpp, declared in spdlog_gem.h.
 *
 * Fills no slot. Logging is not the platform's to provide -- there is no
 * console slot to fill -- so a backend joins the log the way any other would,
 * through mg_register_logger, and this gem is only what holds that
 * registration for as long as its image is mapped.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/gems/state.h"
#include "spdlog_gem.h"

/* ========================================================================== */
/* The logger                                                                 */
/* ========================================================================== */

/**
 * @brief The engine's severity as the sink's.
 * @param level The engine level the message was logged at.
 * @return The sink level to write it at; INFO for anything past the ladder.
 */
static mg_spdlog_level mg_spdlog_level_from_(const mg_log_level level) {
    switch (level) {
        case MG_LOG_LEVEL_TRACE:
            return MG_SPDLOG_TRACE;
        case MG_LOG_LEVEL_DEBUG:
            return MG_SPDLOG_DEBUG;
        case MG_LOG_LEVEL_INFO:
            return MG_SPDLOG_INFO;
        case MG_LOG_LEVEL_WARN:
            return MG_SPDLOG_WARN;
        case MG_LOG_LEVEL_ERROR:
            return MG_SPDLOG_ERROR;
        case MG_LOG_LEVEL_FATAL:
            return MG_SPDLOG_CRITICAL;
        case MG_LOG_LEVEL_COUNT:
        default:
            return MG_SPDLOG_INFO;
    }
}

/**
 * @brief An mg_logger over spdlog: the line the engine already formatted,
 * handed to the sink with the call site that wrote it still attached.
 * @param level The severity of the message.
 * @param file The file the message came from.
 * @param line The line the message came from.
 * @param message The message itself.
 */
static void mg_spdlog_logger_(
    const mg_log_level level,
    const char* file,
    const i32 line,
    const char* message) {
    mg_spdlog_write(mg_spdlog_level_from_(level), file, line, message);
}

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it: the sink first, so the
 * registration that follows never points at one that is not standing.
 * @param owner The app the gem was loaded into.
 */
static void spdlog_on_load(mg_app* owner) {
    MG_UNUSED(owner);

    mg_spdlog_open_sink();
    mg_register_logger(mg_spdlog_logger_);
}

/**
 * @brief Tears the gem down when the engine unloads it.
 * @param owner The app the gem was loaded into.
 */
static void spdlog_on_unload(mg_app* owner) {
    MG_UNUSED(owner);

    /* Unregistered before the sink closes, and both before this image is
     * unmapped: a logger left on the log would be dispatched to through code
     * that is no longer there. The engine's built-in default takes the line
     * back the moment this one steps off. */
    mg_unregister_logger(mg_spdlog_logger_);
    mg_spdlog_close_sink();
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "spdlog",
    "1.0",
    "spdlog logging backend",
    "Jeremy Hummel",
    MG_GEM_REQUIRES_NOTHING,
    spdlog_on_load,
    NULL,
    NULL,
    spdlog_on_unload)

/* No MG_GEM_FILL_SLOTS block: this gem fills no slot. */

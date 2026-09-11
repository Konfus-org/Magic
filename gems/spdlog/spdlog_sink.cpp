/**
 * @file spdlog_sink.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The sink itself: the one spdlog logger this gem writes through. The
 * only C++ in the gem, and it sees no Magic header -- everything it trades
 * with the C half is declared in spdlog_gem.h.
 *
 * Nothing throws out of this file. The caller on the other side of every
 * function here is C, and an exception unwinding into it is undefined, so
 * each entry point is total: a failure leaves the sink empty and the engine's
 * own default writer keeps the log.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "spdlog_gem.h"

#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/spdlog.h>

#include <memory>

namespace {

/* The gem's logger. Built by hand rather than through spdlog::stdout_color_mt,
 * which files it in spdlog's global registry under its name: that registry is
 * static in this image, and a name already taken is an exception rather than a
 * second logger. Held here, a reload gets a clean one every time. */
std::shared_ptr<spdlog::logger> g_logger;

/**
 * @brief The sink severity as spdlog's own.
 * @param level The level the C half asked for.
 * @return The spdlog level to log at.
 */
spdlog::level::level_enum to_spdlog_level(const mg_spdlog_level level) {
    switch (level) {
        case MG_SPDLOG_TRACE:
            return spdlog::level::trace;
        case MG_SPDLOG_DEBUG:
            return spdlog::level::debug;
        case MG_SPDLOG_INFO:
            return spdlog::level::info;
        case MG_SPDLOG_WARN:
            return spdlog::level::warn;
        case MG_SPDLOG_ERROR:
            return spdlog::level::err;
        case MG_SPDLOG_CRITICAL:
            return spdlog::level::critical;
    }
    return spdlog::level::info;
}

} // namespace

void mg_spdlog_open_sink(void) {
    if (g_logger) {
        return;
    }

    try {
        auto sink = std::make_shared<spdlog::sinks::stdout_color_sink_mt>();
        g_logger = std::make_shared<spdlog::logger>("magic", std::move(sink));

        /* Wide open: which severities are logged at all is the engine's call,
         * made at compile time in the MG_* macros, so a line that reaches
         * here has already passed the only gate that matters. Filtering again
         * here would only be a second place to go looking when one goes
         * missing. */
        g_logger->set_level(spdlog::level::trace);

        /* %^...%$ is the colored range, %s:%# the file and line the engine
         * handed over as a source location rather than as text. */
        g_logger->set_pattern("%^[%T.%e] [%l] %s:%# %v%$");

        /* A FATAL is the last thing a process says, and the assert trap that
         * follows it would otherwise take a buffered line down with it. */
        g_logger->flush_on(spdlog::level::err);
    } catch (...) {
        g_logger.reset();
    }
}

void mg_spdlog_write(
    const mg_spdlog_level level,
    const char* file,
    const int line,
    const char* message) {
    if (!g_logger) {
        return;
    }

    try {
        /* "{}" and not message: the engine has already expanded the call
         * site's format, so a brace left in the finished line is data, not a
         * placeholder to go looking for an argument for. */
        g_logger->log(spdlog::source_loc {file, line, ""}, to_spdlog_level(level), "{}", message);
    } catch (...) {
        // A line that cannot be written is not worth taking the process for.
    }
}

void mg_spdlog_close_sink(void) {
    if (!g_logger) {
        return;
    }

    try {
        g_logger->flush();
    } catch (...) {
        // Dropped below regardless; there is nowhere left to report this.
    }

    g_logger.reset();
}

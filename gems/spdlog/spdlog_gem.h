/**
 * @file spdlog_gem.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief What the gem's two halves trade across the language boundary. The
 * gem proper is C -- the loader binds C, and an mg_logger is a C function
 * pointer -- while spdlog is a C++ library, so the sink lives in
 * spdlog_sink.cpp and everything it offers the C side is declared here.
 *
 * Deliberately free of Magic headers: this is the one header the gem's C++
 * translation unit includes, and Magic's headers are C, which a C++ front end
 * would give C++ linkage. The engine's vocabulary stops at spdlog_gem.c,
 * which translates it into the names below.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/* ========================================================================== */
/* Severities                                                                 */
/* ========================================================================== */

/**
 * @brief The severities the sink speaks: spdlog's own ladder, named here so
 * neither half has to include the other's headers to say one. Spelled
 * MG_SPDLOG_ and not SPDLOG_LEVEL_, which spdlog itself has already taken as
 * macros. spdlog_gem.c maps mg_log_level onto this in a switch the compiler
 * checks for exhaustiveness, so neither ladder can quietly outgrow the other.
 */
typedef enum mg_spdlog_level {
    MG_SPDLOG_TRACE,
    MG_SPDLOG_DEBUG,
    MG_SPDLOG_INFO,
    MG_SPDLOG_WARN,
    MG_SPDLOG_ERROR,
    MG_SPDLOG_CRITICAL
} mg_spdlog_level;

/* ========================================================================== */
/* The sink (spdlog_sink.cpp)                                                 */
/* ========================================================================== */

/**
 * @brief Stands the sink up: one logger over two sinks, colored stdout and a
 * rotating file, held by this gem rather than put in spdlog's global
 * registry, which is keyed by name and would refuse the second load of a
 * hot-reloaded image. Safe to call again; a sink that fails to come up
 * leaves the gem writing nowhere, and the engine's own default keeps talking.
 * @param log_file Where the rotating file goes, missing folders and all;
 * NULL or empty asks for the console alone. A file that cannot be opened
 * costs the file half only -- the console keeps the log either way. The path
 * is read here and not kept, so the caller's buffer is free afterwards.
 */
void mg_spdlog_open_sink(const char* log_file);

/**
 * @brief Writes one line the engine has already formatted.
 * @param level The severity to write it at.
 * @param file The file the MG_* call was written in.
 * @param line The line it was written on.
 * @param message The finished text; no longer a format string.
 */
void mg_spdlog_write(mg_spdlog_level level, const char* file, int line, const char* message);

/**
 * @brief Flushes what is buffered and drops the sink. After it, a write is a
 * no-op rather than a call into a torn-down logger.
 */
void mg_spdlog_close_sink(void);

#ifdef __cplusplus
}
#endif

/**
 * @file logging.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The MG_* log macros, and the loggers they dispatch through.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/array.h"
#include "magic/enums.h"
#include "magic/export.h"
#include "magic/memory/alloc.h"
#include "magic/primitives.h"

/* A clang/gcc extension, so guarded: a consumer's toolchain is not this
 * project's to choose. */
#if defined(__clang__) || defined(__GNUC__)
    /** @brief Checks a printf-style function's call sites against its format. */
    #define MG_PRINTF_FORMAT(fmt_index, first_arg)                                                 \
        __attribute__((format(printf, fmt_index, first_arg)))
#else
    /** @brief Checks a printf-style function's call sites against its format. */
    #define MG_PRINTF_FORMAT(fmt_index, first_arg)
#endif

/* Compile-time severity gates, set from the MG_LOG_* build options. Always
 * defined because -Wundef is an error here. MG_FATAL has no gate: it is the
 * last thing a process says. */
#ifndef MG_ERROR_ENABLED
    #define MG_ERROR_ENABLED 1
#endif

#ifndef MG_WARNING_ENABLED
    #define MG_WARNING_ENABLED 1
#endif

#ifndef MG_INFO_ENABLED
    #define MG_INFO_ENABLED 1
#endif

#ifndef MG_DEBUG_ENABLED
    #define MG_DEBUG_ENABLED 0
#endif

#ifndef MG_TRACE_ENABLED
    #define MG_TRACE_ENABLED 0
#endif

#define MG_LOG_1_(level, ...) mg_trace((level), __FILE__, __LINE__, __VA_ARGS__)

/* The dead branch is the point: arguments are still parsed and type-checked, so
 * a variable only ever named in a disabled log call does not draw an
 * unused-variable error. Both arms are void, and C emits only the taken one. */
#define MG_LOG_0_(level, ...) (0 ? MG_LOG_1_(level, __VA_ARGS__) : (void)0)

/* Two levels, so the gate expands to 1 or 0 before it is pasted onto a name.
 * Both ends are pasted, because the name being reached for is MG_LOG_1_ and
 * the trailing underscore has to survive the paste. */
#define MG_LOG_PICK_(enabled, ...) MG_LOG_##enabled##_(__VA_ARGS__)
#define MG_LOG_(enabled, ...) MG_LOG_PICK_(enabled, __VA_ARGS__)

#define MG_FATAL(...) MG_LOG_1_(MG_LOG_LEVEL_FATAL, __VA_ARGS__)
#define MG_ERROR(...) MG_LOG_(MG_ERROR_ENABLED, MG_LOG_LEVEL_ERROR, __VA_ARGS__)
#define MG_WARN(...) MG_LOG_(MG_WARNING_ENABLED, MG_LOG_LEVEL_WARN, __VA_ARGS__)
#define MG_INFO(...) MG_LOG_(MG_INFO_ENABLED, MG_LOG_LEVEL_INFO, __VA_ARGS__)
#define MG_DEBUG(...) MG_LOG_(MG_DEBUG_ENABLED, MG_LOG_LEVEL_DEBUG, __VA_ARGS__)
#define MG_TRACE(...) MG_LOG_(MG_TRACE_ENABLED, MG_LOG_LEVEL_TRACE, __VA_ARGS__)

/** @brief How many loggers one log dispatches to. */
#define MG_MAX_LOGGERS 15

/** @brief Every severity, for the mg_log_level mint below. */
#define MG_LOG_LEVEL_VALUES_(X)                                                                    \
    X(MG_LOG_LEVEL_TRACE)                                                                          \
    X(MG_LOG_LEVEL_DEBUG)                                                                          \
    X(MG_LOG_LEVEL_INFO)                                                                           \
    X(MG_LOG_LEVEL_WARN)                                                                           \
    X(MG_LOG_LEVEL_ERROR)                                                                          \
    X(MG_LOG_LEVEL_FATAL)

/** @brief The severity level of a log message. */
MG_DEFINE_ENUM(mg_log_level, MG_LOG_LEVEL_COUNT, MG_LOG_LEVEL_VALUES_);

/**
 * @brief A logging backend.
 * The call site comes in as parameters because __FILE__ and __LINE__ expand
 * where the MG_* macro was written.
 */
typedef void (*mg_logger)(mg_log_level level, const char* file, i32 line, const char* message);

MG_DEFINE_ARRAY(mg_logger, NULL);

/**
 * @brief Logging context: the loggers a log dispatches to.
 * The array owns its slots: mg_open_log allocates them, mg_close_log
 * releases them. A struct copy of an open log shares the original's slots, so
 * close only one of the two.
 * A log with nothing registered is not a silent one -- it writes through a
 * built-in stdio default, which stands down as soon as the first logger
 * registers. Silence is mg_use_log(NULL).
 */
typedef struct mg_log {
    mg_logger_array loggers; /**< Registered mg_logger items, in registration order. */
} mg_log;

/**
 * @brief A log over static logger storage, as a constant expression: the one
 * form that initializes a file-scope log, like the app's. Ready with no
 * mg_open_log, and never mg_close_log one -- its storage was never
 * allocated, so there is nothing to free; clear the loggers instead.
 */
#define mg_log_construct() {.loggers = mg_array_construct(mg_logger, MG_MAX_LOGGERS)}

/**
 * @brief Prepares a log in storage the caller owns, allocating its logger
 * slots, and puts it in use: an opened log is the one dispatched to, with no
 * second call to make. Out of memory opens a log that drops every
 * registration.
 */
void mg_open_log(mg_log* log);

/**
 * @brief Releases what a log owns; does not free the log itself. A log in
 * use stands down as it closes, so nothing dispatches through freed slots.
 */
void mg_close_log(mg_log* log);

/**
 * @brief Points the MG_* macros at the log they dispatch through, replacing
 * whatever log held the post; NULL silences them. The app points this at its
 * own log as it opens and back at NULL as it closes, so this module never
 * has to know the app. Borrowed until replaced.
 */
void mg_use_log(mg_log* log);

/**
 * @brief Adds a logger to the log in use, dispatched to in registration
 * order. A registration past the log's capacity is dropped. The first one
 * registered retires the built-in stdio default, so a backend that joins the
 * log replaces it rather than printing alongside it.
 */
MG_EXPORT void mg_register_logger(mg_logger logger);

/**
 * @brief Removes a logger from the log in use, to no longer be dispatched to.
 * A gem unregisters its own in on_unload, before its image goes: a logger
 * left behind would be dispatched to through code that is no longer mapped.
 * Taking the last one out puts the built-in default back.
 */
MG_EXPORT void mg_unregister_logger(mg_logger logger);

/**
 * @brief Dispatches a message to every logger on the log in use, or to the
 * built-in default when none is registered.
 * Called through the MG_* macros, which fill in file and line.
 */
MG_EXPORT void mg_trace(mg_log_level level, const char* file, i32 line, const char* format, ...)
    MG_PRINTF_FORMAT(4, 5);

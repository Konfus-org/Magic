/**
 * @file logging.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/debug/logging.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/debug/logging.h"
#include <stdarg.h>
#include <stdio.h>

/** @brief Longest message a single MG_* call can produce. */
#define MAX_MESSAGE_LENGTH 32000

/* The log in use: this module's own pointer, because mg_trace has no mg_log
 * parameter -- the MG_* macros are written at call sites with no log to
 * pass. A pointer and not the app, so dispatching needs no knowledge of who
 * owns the log: the app points this at its log as it opens and nothing here
 * ever asks whose it was. NULL, the state before open and after close,
 * silences the macros. */
static mg_log* g_log = NULL;

/**
 * @brief What a log with no logger registered writes through: plain stdio, so
 * the engine is heard before the first gem loads and after the last one goes.
 * Never registered and never registerable -- it speaks only while nobody
 * else does, the way a gem slot answers with its default until a gem fills
 * it. A switch and not a table of tags, because -Wswitch-enum is what makes
 * a level added to mg_log_level a compile error here rather than a line that
 * quietly prints as UNKNOWN.
 */
static void mg_default_logger_(
    const mg_log_level level,
    const char* file,
    const i32 line,
    const char* message) {
    switch (level) {
        case MG_LOG_LEVEL_TRACE:
            printf("[TRACE] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_DEBUG:
            printf("[DEBUG] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_INFO:
            printf("[INFO] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_WARN:
            printf("[WARN] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_ERROR:
            printf("[ERROR] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_FATAL:
            printf("[FATAL] %s:%d: %s\n", file, line, message);
            break;
        case MG_LOG_LEVEL_COUNT:
        default:
            printf("[UNKNOWN] %s:%d: %s\n", file, line, message);
    }

    /* Flushed every line: this is the fallback writer, whose job is being
     * heard, and a redirected stdout is fully buffered -- the assert trap
     * that follows a FATAL would otherwise take the words down with the
     * process. */
    fflush(stdout);
}

void mg_open_log(mg_log* log) {
    MG_ASSERT_RETURN_IF(log, "Tried to initialize a null log");

    /* The array is the storage: opening allocates the slots, closing frees
     * them. Out of memory leaves a zero-capacity array, which register treats
     * as full -- registrations drop rather than crash. */
    log->loggers = mg_logger_array_new(MG_MAX_LOGGERS);

    // An opened log is the one in use; no second call to make.
    mg_use_log(log);
}

void mg_close_log(mg_log* log) {
    MG_ASSERT_RETURN_IF(log, "Tried to destroy a null log");
    mg_logger_array_free(&log->loggers);

    /* A closed log's slots are freed, so were it left in use the next
     * MG_INFO would dispatch through freed memory. Only the log holding the
     * post gives it up. */
    if (g_log == log) {
        g_log = NULL;
    }
}

void mg_use_log(mg_log* log) {
    g_log = log;
}

void mg_register_logger(const mg_logger logger) {
    MG_ASSERT_RETURN_IF(g_log, "Tried to register a logger with no log in use");
    MG_ASSERT_RETURN_IF(logger, "Tried to register a null logger");

    /* Appended, so registration order is dispatch order. A full log drops the
     * registration rather than walking into the append's assert: the array is
     * fixed, and evicting someone else's logger would be the worse failure. */
    if (mg_buffer_full(&g_log->loggers)) {
        MG_WARN("Log is full at %d loggers; registration dropped.", MG_MAX_LOGGERS);
        return;
    }
    mg_logger_array_push(&g_log->loggers, logger);
}

void mg_unregister_logger(const mg_logger logger) {
    MG_ASSERT_RETURN_IF(g_log, "Tried to unregister a logger with no log in use");
    MG_ASSERT_RETURN_IF(logger, "Tried to unregister a null logger");

    /* The swapping remove: the last logger fills the gap, O(1). Dispatch
     * order after an unregister is registration order no longer -- every
     * logger still gets every message; none depends on its turn. */
    mg_logger_array* loggers = &g_log->loggers;
    for (ind i = 0; i < loggers->count; i++) {
        if (*mg_logger_array_at(loggers, i) != logger) {
            continue;
        }
        mg_logger_array_remove(loggers, i);
        return;
    }
}

void mg_trace(const mg_log_level level, const char* file, const i32 line, const char* format, ...) {
    /* Returns rather than asserting: MG_ASSERT reports through MG_FATAL,
     * which is a call straight back into here. No log in use is silence by
     * design -- the state before an app opens and after it closes. */
    if (g_log == NULL) {
        return;
    }

    char message[MAX_MESSAGE_LENGTH] = {0};
    va_list arg_ptr;
    va_start(arg_ptr, format);
    vsnprintf(message, MAX_MESSAGE_LENGTH, format, arg_ptr);
    va_end(arg_ptr);

    /* An empty log is not a silent one: with nobody registered the default
     * writer speaks, and the moment a backend registers it stops. That is
     * what keeps a logging gem from printing every line twice, and what
     * keeps the lines before it loads from being lost. */
    if (mg_buffer_empty(&g_log->loggers)) {
        mg_default_logger_(level, file, line, message);
        return;
    }

    mg_logger_array_iter it = mg_logger_array_it(&g_log->loggers);
    const mg_logger* logger;
    while ((logger = mg_logger_array_iter_next(&it)) != NULL) {
        (*logger)(level, file, line, message);
    }
}

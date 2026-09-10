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
     * which is a call straight back into here. Logging with no log in use is
     * a no-op by design, and an empty log skips out before the formatting. */
    if (g_log == NULL || mg_buffer_empty(&g_log->loggers)) {
        return;
    }

    char message[MAX_MESSAGE_LENGTH] = {0};
    va_list arg_ptr;
    va_start(arg_ptr, format);
    vsnprintf(message, MAX_MESSAGE_LENGTH, format, arg_ptr);
    va_end(arg_ptr);

    mg_logger_array_iter it = mg_logger_array_it(&g_log->loggers);
    const mg_logger* logger;
    while ((logger = mg_logger_array_iter_next(&it)) != NULL) {
        (*logger)(level, file, line, message);
    }
}

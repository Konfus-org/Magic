/**
 * @file app.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/app.h, and home of the one app.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/app.h"
#include "magic/commands/builtin.h"
#include "magic/commands/parser.h"
#include "magic/commands/registry.h"
#include "magic/debug/asserts.h"
#include "magic/events/registry.h"
#include "magic/gems/loading.h"
#include "magic/memory/slots.h"
#include "magic/platform/slots.h"
#include "magic/time/delta_time.h"
#include "magic/time/frame.h"

/* The one app, in static storage rather than an allocation: statics are
 * zeroed before main and live for the whole process, so the app is there to
 * hand out before mg_open_app and after mg_close_app. Everything it owns is
 * constructed here, never opened later: constructed storage is static, so
 * nothing below owes an allocation up front or a free on close. Constructed
 * rather than left zeroed because a push checks item_size and a zeroed list
 * has none; every field a construct does not name starts at the zero it
 * should. */
static mg_app g_app = {
    .log = mg_log_construct(),
    .events = mg_event_registry_construct(),
    .cmds = mg_cmd_registry_construct(),
    .gems = mg_list_construct(mg_gem_ptr),
};

static void mg_console_logger(
    const mg_log_level level,
    const char* file,
    const i32 line,
    const char* message) {
    gs_platform_write_to_console(level, file, line, message);
}

static b8 mg_on_command_sent(void* listener, const void* sender, const mg_event* evt) {
    MG_UNUSED(sender);
    const mg_app* app = listener;

    const mg_cmd* cmd = mg_unbox(mg_cmd, evt->data);
    MG_ASSERT_RETURN_VAL_IF(app != NULL, FALSE, "the command listener is the app");
    MG_ASSERT_RETURN_VAL_IF(cmd != NULL, FALSE, "a command event needs a boxed mg_cmd");

    const b8 handled = mg_run_cmd(cmd);
    gs_platform_update(&app->platform);

    return handled;
}

static b8 mg_on_request_exit(void* listener, const void* sender, const mg_event* evt) {
    MG_UNUSED(sender, evt);
    mg_app* app = listener;
    app->state = MG_APP_STATE_EXITING;
    return TRUE;
}

/**
 * @brief Fans a fired event out to the app's on_event callback. Registered,
 * only when the app gave one, for every code ahead of any other listener, and
 * never handles: hearing an event is not consuming it, so the fire always
 * carries on down the chain.
 */
static b8 mg_forward_event(void* listener, const void* sender, const mg_event* evt) {
    const mg_app* app = listener;
    app->callbacks.on_event(sender, evt);
    return FALSE;
}

mg_app* mg_get_app_instance(void) {
    return &g_app;
}

mg_app* mg_open_app(const int argc, const char** argv, const mg_app_callbacks* callbacks) {
    mg_app* app = &g_app;
    MG_ASSERT_RETURN_VAL_IF(
        app->state == MG_APP_STATE_UNINITIALIZED,
        app,
        "the app is already open; there is only the one");

    /* Before anything below, which fires on_init and needs on_event in
     * place to register the forwarder. */
    app->callbacks = callbacks != NULL ? *callbacks : (mg_app_callbacks) {0};

    /* Wire the modules at the app's fields: each keeps a pointer to what it
     * works on and never knows the app. Everything the app owns is already
     * constructed, so wiring is all opening amounts to before content. */
    mg_use_log(&app->log);
    mg_use_event_registry(&app->events);
    mg_use_cmd_registry(&app->cmds);

    // Logging first, so everything after it can be heard.
    mg_register_logger(mg_console_logger);

    // Defaults
    app->name = "Magic";
    app->dirs = (mg_app_dirs) {.asset_folder = "Assets", .gem_folder = "Gems", .root = "."};

    /* Early hook: after the defaults, so an app may override them; before
     * the command line, so the command line overrides the app. */
    if (app->callbacks.on_init)
        app->callbacks.on_init();

    // Commands: the built-ins, then the command line that may run them.
    mg_register_builtin_cmds(app);
    mg_parse_cmd_line(argc, argv);

    // Events: the app's own listeners.
    if (app->callbacks.on_event)
        mg_register_event_listener(MG_EVENT_CODE_ANY, app, mg_forward_event);

    mg_register_event_listener(MG_EVENT_CODE_COMMAND_SENT, app, mg_on_command_sent);
    mg_register_event_listener(MG_EVENT_CODE_APP_REQUEST_EXIT, app, mg_on_request_exit);

    // Gems
    mg_memory_register_gem_slots();
    mg_platform_register_gem_slots();
    app->gems = mg_load_gems(app->dirs.gem_folder, app);

    // Platform
    gs_platform_setup(&app->platform);

    // Done init begin running state!
    app->state = MG_APP_STATE_RUNNING;

    if (app->callbacks.on_start)
        app->callbacks.on_start();

    return app;
}

void mg_close_app(mg_app* app) {
    MG_ASSERT_RETURN_IF(app == &g_app, "close takes the app mg_open_app returned");
    MG_ASSERT_RETURN_IF(app->state != MG_APP_STATE_UNINITIALIZED, "the app is not open");

    /* Before any teardown, so every subsystem is still alive to hear it. */
    if (app->callbacks.on_exit)
        app->callbacks.on_exit();

    // WARNING: ORDER IS IMPORTANT! DO NOT FIDDLE WITH ORDER UNLESS YOU KNOW WHAT YOU ARE DOING!
    mg_event_registry_clear(&app->events);
    mg_cmd_registry_clear(&app->cmds);
    gs_platform_cleanup();
    mg_unload_gems(&app->gems);

    /* The log goes last, so the teardown above can still be heard. Cleared,
     * never closed: a constructed log's storage was never allocated, so
     * there is nothing to free. */
    mg_buffer_clear(&app->log.loggers);

    /* Stand the modules down, so nothing dispatches into a closed app. */
    mg_use_log(NULL);
    mg_use_event_registry(NULL);
    mg_use_cmd_registry(NULL);

    // Back to the opening state, so a later mg_open_app starts clean.
    app->callbacks = (mg_app_callbacks) {0};
    app->name = NULL;
    app->dirs = (mg_app_dirs) {0};
    app->frame = (mg_frame) {0};
    app->platform = (mg_platform_state) {0};
    app->state = MG_APP_STATE_UNINITIALIZED;

    /* Development machinery, so it is compiled in only where it can act: a
     * release build has neither the assert to fire nor the logs to read, and
     * the usage it gathered would be a store nothing loads. */
#if MG_ASSERT_ENABLED
    // Check for memory leak:
    const mg_memory_usage usage = mg_get_mem_usage();
    MG_INFO("Memory allocated over lifetime: %llu bytes", usage.total.allocated);
    MG_INFO("Memory deallocated over lifetime: %llu bytes", usage.total.deallocated);
    MG_ASSERT(
        usage.total.allocated == usage.total.deallocated,
        "Memory leak! The total allocated bytes does not equal the total de-allocated bytes.");
#endif
}

b32 mg_app_pump(mg_app* app) {
    MG_ASSERT_RETURN_VAL_IF(app, FALSE, "app is null");

    mg_next_frame(&app->frame);

    mg_event evt;
    while (gs_platform_poll_event(&evt)) {
        mg_queue_event(NULL, &evt);
    }

    if (!gs_platform_pump(&app->platform))
        app->state = MG_APP_STATE_EXITING;

    mg_process_event_queue();

    if (app->callbacks.on_update)
        app->callbacks.on_update(app->frame.dt);

    while (app->frame.accumulator >= MG_FIXED_DT) {
        if (app->callbacks.on_fixed_update)
            app->callbacks.on_fixed_update(MG_FIXED_DT); // Physics always moves at exactly fixed_dt
        app->frame.accumulator -= MG_FIXED_DT;
    }

    switch (app->state) {
        case MG_APP_STATE_RUNNING:
        case MG_APP_STATE_SUSPENDED:
            return TRUE;
        case MG_APP_STATE_UNINITIALIZED:
        case MG_APP_STATE_EXITING:
            return FALSE;
        case MG_APP_STATE_ERROR:
            MG_ASSERT(FALSE, "Encountered an error! Shutting down...");
            return -1;
        default:
            MG_ASSERT(FALSE, "Invalid state");
            return TRUE;
    }
}

int mg_app_run(const int argc, const char** argv, const mg_app_callbacks* callbacks) {
    mg_app* app = mg_open_app(argc, argv, callbacks);

    /* b32, not b8: mg_app_pump returns -1 on the error state, and a b8 is C's
     * _Bool, which collapses anything nonzero to 1 on assignment -- through a
     * b8 the -1 would arrive as TRUE and the check below could never fire. */
    b32 running = TRUE;
    int exit_code = 0;
    while (running) {
        running = mg_app_pump(app);
        if (running == -1) {
            exit_code = 1;
            break;
        }
    }

    /* On the error path too: the close is what fires on_exit and tears the
     * subsystems down, and an error is not a reason to skip that. */
    mg_close_app(app);
    return exit_code;
}

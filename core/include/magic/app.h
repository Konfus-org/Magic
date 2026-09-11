/**
 * @file app.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The engine's one app: state, dirs, log, registries and gems.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/commands/registry.h"
#include "magic/debug/logging.h"
#include "magic/enums.h"
#include "magic/events/registry.h"
#include "magic/export.h"
#include "magic/gems/gem.h"
#include "magic/primitives.h"
#include "magic/time/frame.h"
#include "platform/state.h"

/* A gate like the log gates: the build passes it as 0 or 1 (see the options in
 * the top-level CMakeLists.txt). The fallback is 0, not the option's default:
 * callbacks oblige an app to define six functions, so a consumer outside the
 * build has to say so explicitly. */
#ifndef MG_USE_CALLBACKS
    #define MG_USE_CALLBACKS 0
#endif

/**
 * @brief Where an app's files live. Every path is borrowed: an app copies
 * none of them and frees none of them, so each has to outlive the app it was
 * handed to. const because nothing here writes through one -- a path is input
 * whether it is being read from or written to.
 */
typedef struct mg_app_dirs {
    const char* root;
    const char* gem_folder;
    const char* asset_folder;
} mg_app_dirs;

/** @brief Every lifecycle state, for the mg_app_state mint below. */
#define MG_APP_STATE_VALUES_(X)                                                                    \
    X(MG_APP_STATE_UNINITIALIZED)                                                                  \
    X(MG_APP_STATE_RUNNING)                                                                        \
    X(MG_APP_STATE_SUSPENDED)                                                                      \
    X(MG_APP_STATE_EXITING)                                                                        \
    X(MG_APP_STATE_ERROR)

/** @brief Where an app is in its lifecycle. Only RUNNING and PAUSED poll. */
MG_DEFINE_ENUM(mg_app_state, MG_APP_STATE_COUNT, MG_APP_STATE_VALUES_);

/**
 * @brief The functions the engine-owned loop drives, carried as pointers
 * because a name cannot cross a library boundary: a symbol resolves when the
 * binary that spells it links, and a shared magic links long before any app
 * exists to define mg_on_ anything. A pointer is plain data and crosses
 * freely. Any member may be NULL, and a NULL callback is skipped, never
 * called. Each member mirrors the mg_on_* function of the same name below,
 * where its timing is documented.
 */
typedef struct mg_app_callbacks {
    void (*on_init)(void);
    void (*on_start)(void);
    void (*on_event)(const void* sender, const mg_event* evt);
    void (*on_update)(ts dt);
    void (*on_fixed_update)(ts fixed_dt);
    void (*on_exit)(void);
} mg_app_callbacks;

/**
 * @brief The engine's top-level object, and the one instance there is: the
 * app-lifetime state -- log, registries, gems, platform -- lives here as
 * fields, and mg_open_app points each module at its field, so a module
 * keeps a pointer and never knows the app. Engine-owned in static storage,
 * so the struct exists for the whole process, with state saying whether it
 * is open. Process-lifetime bookkeeping -- the memory books, the gem slot
 * registry -- lives with its own module instead, since it spans opens. An
 * app owns its log, registries and gems; dirs and name are borrowed.
 */
typedef struct mg_app {
    const char* name;
    mg_app_state state;
    mg_frame frame;
    mg_platform_state platform;
    mg_app_dirs dirs;
    mg_log log;
    mg_event_registry events;
    mg_cmd_registry cmds;
    mg_gem_list gems;
    mg_app_callbacks callbacks;
} mg_app;

/**
 * @brief The one app, the same one every call: never NULL, open or not.
 * The convenience the engine's own logging, events and command functions
 * reach state through, and an app's way to the same -- querying input off
 * platform, say -- from a call site no app pointer reaches. Prefer the
 * pointer already in hand where there is one: reaching into the singleton is
 * for ease of use, not the default.
 */
MG_EXPORT mg_app* mg_get_app_instance(void);

/**
 * @brief Opens the app: registries, log, gems, platform, in that order, then
 * on_start. Allocates nothing for the app itself -- the singleton's storage
 * is static -- so unlike a _new there is no NULL to check and no free owed;
 * mg_close_app is the other end.
 * @param argc Argument count, handed through from main.
 * @param argv Argument values, handed through from main.
 * @param callbacks The functions the loop drives. Copied, so the struct may
 * live on the caller's stack; NULL, or any NULL member, skips that callback,
 * as for an app that drives the pump itself.
 * @return The opened app: mg_get_app_instance's answer, handed out so a caller passes
 * it along rather than reaching back into the singleton.
 */
MG_EXPORT mg_app* mg_open_app(int argc, const char** argv, const mg_app_callbacks* callbacks);

/**
 * @brief Fires on_exit, then closes everything mg_open_app stood up, stands
 * the modules down, and returns the app to UNINITIALIZED, ready to open
 * again.
 * @param app The app mg_open_app returned.
 */
MG_EXPORT void mg_close_app(mg_app* app);

/**
 * @brief A one frame update, should be called every frame the app is running.
 * @return True while the app is running or paused, false once it is no longer
 * pollable, -1 for error.
 */
MG_EXPORT b32 mg_app_pump(mg_app* app);

/**
 * @brief An app's whole lifetime in one call: opens it, pumps it until it is
 * no longer pollable, then closes it.
 * @param argc Argument count, handed through from main.
 * @param argv Argument values, handed through from main.
 * @param callbacks The functions the loop drives. Copied, so the struct may
 * live on the caller's stack; NULL, or any NULL member, skips that callback.
 * @return The process exit code: 0 after a clean run, 1 after the error
 * state.
 */
MG_EXPORT int mg_app_run(int argc, const char** argv, const mg_app_callbacks* callbacks);

/* The name-based face of the callbacks, for apps that want to define six
 * functions and no main. The engine never references these symbols; the stub
 * in core/main/main.c does -- a static library compiled into the app itself,
 * beside the definitions, which is the one place the names can resolve. Its
 * main() gathers the six into an mg_app_callbacks and hands them to
 * mg_app_run. */
#if MG_USE_CALLBACKS

/** @brief Runs once, before the app has fully opened and before on start. */
extern void mg_on_init(void);

/** @brief Runs once, after the app has fully opened and before the first pump. */
extern void mg_on_start(void);

/**
 * @brief Hears every event the registry fires, before any other listener and
 * whether or not one of them goes on to handle it.
 * @param evt The event, never NULL.
 */
extern void mg_on_event(const void* sender, const mg_event* evt);

/**
 * @brief Runs once per pump while the app is running, after that pump's
 * events.
 * @param dt Milliseconds since the previous pump; 0 on the first.
 */
extern void mg_on_update(ts dt);

/**
 * @brief Runs once per owed fixed step. Declared so the loop can call it the
 * day fixed stepping lands; until then it never fires.
 * @param fixed_dt The fixed timestep to advance by, in milliseconds.
 */
extern void mg_on_fixed_update(ts fixed_dt);

/**
 * @brief Runs once, as the app begins to close and before any teardown, so
 * every subsystem is still alive to hear it.
 */
extern void mg_on_exit(void);

#endif

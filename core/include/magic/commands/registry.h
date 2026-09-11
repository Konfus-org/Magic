/**
 * @file registry.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The registry half of commands, in the manner of the event registry:
 * handlers keyed by command code, run through mg_run_cmd. A command can also
 * arrive as an event: fire MG_EVENT_CODE_COMMAND_SENT with an mg_cmd boxed
 * as the data, and the app runs it and updates the platform from the state
 * the handlers set.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/commands/parser.h"
#include "magic/enums.h"
#include "magic/export.h"
#include "magic/primitives.h"

/** @brief Every command code, for the mg_cmd_code mint below. */
#define MG_CMD_CODE_VALUES_(X)                                                                     \
    X(MG_CMD_CODE_NONE)                                                                            \
    X(MG_CMD_CODE_HELP)                                                                            \
    X(MG_CMD_CODE_NAME)                                                                            \
    X(MG_CMD_CODE_DIRS)                                                                            \
    X(MG_CMD_CODE_WINDOW)

/**
 * @brief The commands the engine itself understands. A command's code is a
 * plain u32 everywhere it travels, not this enum, so a user mints their own
 * codes starting at MG_CMD_CODE_COUNT -- the first code free for a user's
 * own -- and registers handlers for them the same way the engine registers
 * these.
 */
MG_DEFINE_ENUM(mg_cmd_code, MG_CMD_CODE_COUNT, MG_CMD_CODE_VALUES_);

/**
 * @brief One command to run: which, and its arguments. args is borrowed
 * from whoever built the command -- the parser's argv window, or a sender's
 * own storage -- and only for the length of the run.
 */
typedef struct mg_cmd {
    u32 code;
    mg_cstr_array args;
} mg_cmd;

/**
 * @brief Runs one command it was registered for. Checks cmd->args.count
 * before trusting any arguments slot, like any reader of the parser's window.
 * No sender, unlike an event handler: a command is an instruction, not an
 * occurrence, so who asked for it is nobody's business.
 * @param listener Whoever registered the handler, handed back as given: the
 * handler's way to its own state, since a bare function carries none.
 * @param cmd The command, never NULL.
 * @return True when the command was handled, which ends the run.
 */
typedef b8 (*mg_cmd_handler)(void* listener, const mg_cmd* cmd);

/** @brief One registration: which code, who listens, whose handler. */
typedef struct mg_cmd_binding {
    u32 code;
    void* listener;
    mg_cmd_handler handler;
} mg_cmd_binding;

MG_DEFINE_LIST(mg_cmd_binding, NULL);

/**
 * @brief Everything a registry keeps about commands: every binding
 * registration has made. A flat list scanned by code, not an array indexed
 * by it like the event registry's: a code is an open u32 a user mints past
 * MG_CMD_CODE_COUNT, so there is no count to size an index by. Registrations
 * stay few enough that the scan costs nothing. The app owns the one instance
 * as a field; construct it, and clear it when its owner closes -- there is
 * no init.
 */
typedef struct mg_cmd_registry {
    mg_cmd_binding_list bindings;
} mg_cmd_registry;

/**
 * @brief A command registry as a constant expression, which a file-scope
 * registry like the app's requires: one empty list, ready as it stands.
 */
#define mg_cmd_registry_construct() {.bindings = mg_list_construct(mg_cmd_binding)}

/**
 * @brief Points the registration and run functions at the registry they
 * work on, replacing whatever registry held the post; NULL stands the module
 * down. The app aims this at its own registry as it opens and back at NULL
 * as it closes. Borrowed until replaced.
 */
MG_EXPORT void mg_use_cmd_registry(mg_cmd_registry* cmds);

/** @brief Clears a registry: every binding registration has made. */
MG_EXPORT void mg_cmd_registry_clear(mg_cmd_registry* cmds);

/**
 * @brief Registers a handler to run when cmd_code's command does.
 * @param cmd_code The command to handle: a built-in code, or a user's own.
 * @param listener Handed to the handler on every run, as given; usually the
 * object the command acts on. Borrowed until unregistered.
 * @param handler The handler to register.
 */
MG_EXPORT void mg_register_cmd_handler(u32 cmd_code, void* listener, mg_cmd_handler handler);

/**
 * @brief Unregisters one earlier registration: the pair of listener and
 * handler, so two listeners sharing a handler unhook separately.
 * @param cmd_code The code the handler was registered for.
 * @param listener The listener the handler was registered with.
 * @param handler The handler to unregister.
 */
MG_EXPORT void mg_unregister_cmd_handler(u32 cmd_code, void* listener, mg_cmd_handler handler);

/**
 * @brief Runs a command through every handler registered for its code, in
 * registration order, until one reports it handled.
 * @param cmd The command to run.
 * @return True when some handler handled it; false for a code nobody has.
 */
MG_EXPORT b8 mg_run_cmd(const mg_cmd* cmd);

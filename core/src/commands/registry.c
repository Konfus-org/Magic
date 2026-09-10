/**
 * @file registry.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Command registration. Implements magic/commands/registry.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/commands/registry.h"
#include "magic/debug/asserts.h"

/* The registry in use: this module's own pointer, so registration and
 * running need no knowledge of who owns the registry -- the app aims this at
 * its own as it opens and nothing here ever asks whose it was. NULL, the
 * state before open and after close, makes registering loud and running
 * quiet. */
static mg_cmd_registry* g_cmds = NULL;

void mg_use_cmd_registry(mg_cmd_registry* cmds) {
    g_cmds = cmds;
}

void mg_cmd_registry_clear(mg_cmd_registry* cmds) {
    MG_ASSERT_RETURN_IF(cmds != NULL, "cmds must not be NULL");
    mg_cmd_binding_list_free(&cmds->bindings);
}

void mg_register_cmd_handler(const u32 cmd_code, void* listener, const mg_cmd_handler handler) {
    MG_ASSERT_RETURN_IF(g_cmds != NULL, "no command registry is in use");
    MG_ASSERT_RETURN_IF(handler != NULL, "handler must not be NULL");
    mg_cmd_binding_list_push(
        &g_cmds->bindings,
        (mg_cmd_binding) {.code = cmd_code, .listener = listener, .handler = handler});
}

void mg_unregister_cmd_handler(const u32 cmd_code, void* listener, const mg_cmd_handler handler) {
    if (g_cmds == NULL) {
        return;
    }

    /* Compared field by field, not through index_of: the binding struct pads
     * after code, and index_of compares bytes, padding included. */
    ind index = 0;
    mg_cmd_binding* binding;
    mg_foreach(binding, &g_cmds->bindings) {
        if (binding->code == cmd_code && binding->listener == listener
            && binding->handler == handler) {
            mg_buffer_remove(&g_cmds->bindings, index);
            return;
        }
        index++;
    }
}

b8 mg_run_cmd(const mg_cmd* cmd) {
    MG_ASSERT_RETURN_VAL_IF(cmd != NULL, FALSE, "cmd must not be NULL");

    // No registry in use is nobody handling, not an error.
    if (g_cmds == NULL) {
        return FALSE;
    }

    mg_cmd_binding* binding;
    mg_foreach(binding, &g_cmds->bindings) {
        if (binding->code == cmd->code && binding->handler(binding->listener, cmd)) {
            return TRUE;
        }
    }
    return FALSE;
}

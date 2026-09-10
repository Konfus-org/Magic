/**
 * @file builtin.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/commands/builtin.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/commands/builtin.h"
#include "magic/collections/string.h"
#include "magic/commands/registry.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/files/parsing.h"

/** @brief MG_CMD_CODE_HELP: logs every built-in command. */
static b8 mg_on_cmd_help(void* listener, const mg_cmd* cmd) {
    MG_UNUSED(listener);
    MG_UNUSED(cmd);
    MG_INFO("h, help, Get info on available commands");
    MG_INFO("n, name, Define name of application: --name <name>");
    MG_INFO("w, window, Define window properties: --window <width> <height> <mode>");
    MG_INFO("d, dirs, Define directories: --dirs <root> <gems> <assets>");
    return TRUE;
}

/** @brief MG_CMD_CODE_NAME: renames the app, window title included. */
static b8 mg_on_cmd_name(void* listener, const mg_cmd* cmd) {
    mg_app* app = listener;
    MG_ASSERT_RETURN_VAL_IF(app != NULL, FALSE, "the built-ins listen as the app");
    if (cmd->args.count < 1) {
        MG_WARN("--name needs a value");
        return FALSE;
    }

    mg_cstr_array args = cmd->args;
    app->name = *mg_cstr_array_at(&args, 0);
    app->platform.name = mg_view_cstr(app->name);
    return TRUE;
}

/** @brief MG_CMD_CODE_DIRS: points the app at its folders. */
static b8 mg_on_cmd_dirs(void* listener, const mg_cmd* cmd) {
    mg_app* app = listener;
    MG_ASSERT_RETURN_VAL_IF(app != NULL, FALSE, "the built-ins listen as the app");
    if (cmd->args.count < 3) {
        MG_WARN("--dirs needs 3 values");
        return FALSE;
    }

    mg_cstr_array args = cmd->args;
    app->dirs.root = *mg_cstr_array_at(&args, 0);
    app->dirs.gem_folder = *mg_cstr_array_at(&args, 1);
    app->dirs.asset_folder = *mg_cstr_array_at(&args, 2);
    return TRUE;
}

/** @brief MG_CMD_CODE_WINDOW: sets the platform state's window shape. */
static b8 mg_on_cmd_window(void* listener, const mg_cmd* cmd) {
    mg_app* app = listener;
    MG_ASSERT_RETURN_VAL_IF(app != NULL, FALSE, "the built-ins listen as the app");
    if (cmd->args.count < 3) {
        MG_WARN("--window needs 3 values");
        return FALSE;
    }

    mg_cstr_array args = cmd->args;
    app->platform.window.window_size.width = mg_parse_u32(*mg_cstr_array_at(&args, 0), 0);
    app->platform.window.window_size.height = mg_parse_u32(*mg_cstr_array_at(&args, 1), 0);
    app->platform.window.window_mode = mg_parse_u8(*mg_cstr_array_at(&args, 2), 0);
    return TRUE;
}

void mg_register_builtin_cmds(mg_app* app) {
    MG_ASSERT_RETURN_IF(app != NULL, "app must not be NULL");
    mg_register_cmd_handler(MG_CMD_CODE_HELP, app, mg_on_cmd_help);
    mg_register_cmd_handler(MG_CMD_CODE_NAME, app, mg_on_cmd_name);
    mg_register_cmd_handler(MG_CMD_CODE_DIRS, app, mg_on_cmd_dirs);
    mg_register_cmd_handler(MG_CMD_CODE_WINDOW, app, mg_on_cmd_window);
}

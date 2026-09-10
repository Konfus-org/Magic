/**
 * @file builtin.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The engine's own command handlers -- help, name, dirs, window --
 * behind one registration call. The handlers themselves stay private: they
 * are reached the way every command is, by code through mg_run_cmd.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/app.h"
#include "magic/export.h"

/**
 * @brief Registers every built-in command handler, with app as its listener.
 * mg_open_app calls it before the command line scan that runs them; clearing
 * the registry in use unhooks everything.
 * @param app The app the built-in commands act on.
 */
MG_EXPORT void mg_register_builtin_cmds(mg_app* app);

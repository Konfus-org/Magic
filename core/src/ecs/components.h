/**
 * @file components.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-14
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/string.h"

void mg_register_component(mg_str_view name);
void mg_register_component_interface(mg_str_view name);
void mg_setup_builtin_components();
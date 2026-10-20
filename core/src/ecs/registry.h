/**
 * @file registry.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-14
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/string.h"

typedef struct mg_component {
    mg_str_view name;
    void* interface;
    void* implementation;
    mg_destructor destructor;
} mg_component;

MG_DEFINE_LIST(mg_component);

typedef struct mg_component_registry {
    mg_component_list registrations;
} mg_component_registry;

mg_component_registry mg_construct_component_registry();
void mg_clear_component_registry(mg_component_registry* registry);
void mg_use_component_registry(mg_component_registry* registry);

/** @registers a component into the registry. */
void mg_register_component(
    mg_str_view name,
    void* interface,
    void* implementation,
    mg_destructor destructor);

/** @brief Unregisters a component from the registry and destroys its implementation. */
void mg_unregister_component(mg_str_view name);

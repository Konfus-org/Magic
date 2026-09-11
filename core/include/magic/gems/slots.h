/**
 * @file slots.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The slots the engine leaves for a gem to fill: how an api declares
 * one, how a family defines and registers one, and how a gem fills one.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/platform/shared_lib.h"

/**
 * @brief One slot this gem fills: the slot as its api header declares it, and
 * the function here that implements it.
 * @param api The declared slot name to fill.
 * @param impl The function that fills it. Needs a name of its own, since the
 * api's header already declares api.
 */
#define MG_GEM_FILL_SLOT(api, impl) MG_PROVIDE_SYMBOL(api, impl)

/**
 * @brief Publishes the slots a gem fills, as the one table the loader binds
 * from. Goes at file scope in exactly one .c of the gem, and may be left out
 * by a gem that fills nothing. The gem-themed spelling of
 * MG_PUBLISH_SYMBOLS, which any loadable library speaks.
 * @code
 * MG_GEM_FILL_SLOTS(
 *     MG_GEM_FILL_SLOT(gs_platform_setup, win32_setup_platform),
 *     MG_GEM_FILL_SLOT(gs_platform_cleanup, win32_shutdown_platform))
 * @endcode
 */
#define MG_GEM_FILL_SLOTS(...) MG_PUBLISH_SYMBOLS(__VA_ARGS__)

/* An engine api declares its slots individually, one macro call per slot: the
 * family's slots.h declares each with MG_DECLARE_GEM_SLOT; the family's
 * slots.c defines each with MG_DEFINE_GEM_SLOT and registers each with
 * MG_REGISTER_GEM_SLOT inside its mg_<family>_register_gem_slots, which
 * mg_open_app calls before any gem loads. An empty parameter list is spelled
 * (void). See magic/platform/slots.h and core/src/platform/slots.c. */

/**
 * @brief Declares a gem slot: one extern function pointer, in the family's
 * slots header. The slot holds the default its define installs until a gem
 * fills it, never NULL: every slot answers, so no call site checks.
 * MG_EXPORT because a slot is a data symbol, not a function: a shared build's
 * linker can route an unattributed function call through the import library
 * on its own, but data has to be declared dllimport to cross, so without this
 * every slot is an undefined symbol at a consumer's link.
 * @param return_type What the slot returns.
 * @param api The slot's name, gs_ prefixed: gem slot, the one prefix that
 * marks a call as going through whatever gem is loaded.
 */
#define MG_DECLARE_GEM_SLOT(return_type, api, ...) extern MG_EXPORT return_type (*api)(__VA_ARGS__)

/**
 * @brief Defines a gem slot pointer at its default. The family's slots.c only.
 * @param return_type What the slot returns.
 * @param api The slot's name, gs_ prefixed.
 * @param default_impl The implementation the slot starts at and unbind
 * restores; never NULL.
 */
#define MG_DEFINE_GEM_SLOT(return_type, api, default_impl, ...)                                    \
    return_type (*api)(__VA_ARGS__) = (default_impl)

/**
 * @brief Registers a defined slot with the engine, so a load can bind gems
 * into it. Goes inside a family's mg_<family>_register_gem_slots. Stringizing
 * the slot keeps the bind name and the pointer from drifting apart.
 * @param api The slot's name, gs_ prefixed.
 */
#define MG_REGISTER_GEM_SLOT(api) mg_register_gem_slot(#api, (mg_lib_fn*)&(api))

/**
 * @brief One slot: the engine pointer a gem fills, the name it fills it under,
 * and the default it falls back to when no gem is in it; never NULL.
 */
typedef struct mg_gem_slot {
    const char* name;
    mg_lib_fn* ptr_address;
    mg_lib_fn default_fn;
} mg_gem_slot;

/**
 * @brief The engine's slots. Borrowed from the registry, and only until the
 * next registration, which may move them; never freed.
 */
typedef struct mg_gem_slot_table {
    const mg_gem_slot* items;
    len count;
} mg_gem_slot_table;

/**
 * @brief Puts one slot in the engine's registry, capturing what the pointer
 * holds now as the default unbind restores. Call before any gem loads, so
 * the pointer still holds its default; MG_REGISTER_GEM_SLOT is the spelling
 * that cannot mistype the name. Registering the same slot again is a no-op;
 * a second slot under a taken name is warned about and dropped.
 * @param name The name a gem fills the slot under.
 * @param ptr_address The slot's pointer, still holding its default.
 */
MG_EXPORT void mg_register_gem_slot(const char* name, mg_lib_fn* ptr_address);

/**
 * @brief Every slot the engine's families have registered. Lives in
 * core/src/gems/slots.c.
 */
MG_EXPORT mg_gem_slot_table mg_gem_slots(void);

/**
 * @brief Empties the registry, giving its storage back. Only once every gem
 * is unbound -- unbinding is what reads the table -- and every pointer is
 * back at its default, which is exactly where mg_close_app stands when it
 * calls this: a later open re-registers the families and recaptures the same
 * defaults, so nothing is lost but the bytes.
 */
MG_EXPORT void mg_clear_gem_slots(void);

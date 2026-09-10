/**
 * @file template_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief A gem to copy, not to load: replace every 'template' and fill in the
 * hooks.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gems/slots.h brings the MG_GEM_FILL_SLOT macros; a gem that fills platform
 * slots also includes magic/platform/slots.h for their declared names. */
#include "magic/gems/slots.h"
#include "magic/gems/state.h"

/* ========================================================================== */
/* Lifecycle -- delete any hook the gem does not need, and hand NULL to        */
/* MG_PROVIDE_GEM in its place.                                             */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it.
 * @param owner The app the gem was loaded into.
 */
static void template_on_load(mg_app* owner) {
    MG_UNUSED(owner);
    /* TODO */
}

/**
 * @brief Saves what must survive a hot reload, before the old image closes.
 * @param owner The app the gem was loaded into.
 * @param state The bytes handed to the new image's on_reloaded.
 */
static void template_on_reload(mg_app* owner, mg_byte_list state) {
    MG_UNUSED(owner, state);
    /* TODO */
}

/**
 * @brief Restores what the old image saved, once the new image is open.
 * @param owner The app the gem was loaded into.
 * @param restore_state The bytes the old image's on_reload saved.
 */
static void template_on_reloaded(mg_app* owner, mg_byte_list restore_state) {
    MG_UNUSED(owner, restore_state);
    /* TODO */
}

/**
 * @brief Tears the gem down when the engine unloads it.
 * @param owner The app the gem was loaded into.
 */
static void template_on_unload(mg_app* owner) {
    MG_UNUSED(owner);
    /* TODO */
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "template",
    "1.0",
    "What this gem is for, in one line.",
    "Jeremy Hummel",
    template_on_load,
    template_on_reload,
    template_on_reloaded,
    template_on_unload)

/* The engine slots this gem fills, if any: one MG_GEM_FILL_SLOT per slot,
 * naming the slot as its api header declares it and the static here that
 * implements it. A gem that fills no slots deletes the whole block;
 * gems/SDL/sdl_gem.c is the worked example.
 *
 * MG_GEM_FILL_SLOTS(
 *     MG_GEM_FILL_SLOT(gs_platform_setup, template_setup_platform),
 *     MG_GEM_FILL_SLOT(gs_platform_cleanup, template_shutdown_platform))
 */

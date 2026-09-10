/**
 * @file slots.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The engine's slot registry: every slot a family has registered, as
 * the table a load binds through. Knows no family; each family's own slots.c
 * defines its slots and registers them here.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/gems/slots.h"
#include "magic/collections/list.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include <string.h>

MG_DEFINE_LIST(mg_gem_slot, NULL);

/* The registry: grows as families register, alive for the whole program and
 * never freed, across app closes and reopens included -- the slots describe
 * pointers whose lifetime is the process's, so the list is this module's
 * own, not the app's. mg_list_construct, not _new: a global's initializer
 * must be a constant expression, which no function call is. */
static mg_gem_slot_list g_slots = mg_list_construct(mg_gem_slot);

void mg_register_gem_slot(const char* name, mg_lib_fn* ptr_address) {
    MG_ASSERT_RETURN_IF(name != NULL && ptr_address != NULL, "a slot needs a name and a pointer");

    /* The same pointer again is a family registering twice: harmless, so
     * silent. The same name over a different pointer is a real collision. */
    mg_gem_slot_list* slots = &g_slots;
    mg_gem_slot_list_iter it = mg_gem_slot_list_it(slots);
    const mg_gem_slot* slot;
    while ((slot = mg_gem_slot_list_iter_next(&it)) != NULL) {
        if (slot->ptr_address == ptr_address) {
            return;
        }
        if (strcmp(slot->name, name) == 0) {
            MG_WARN("Gem slot '%s' is already registered with a different pointer.", name);
            return;
        }
    }

    /* The pointer still holds its default -- registration runs before any gem
     * binds -- so the default is read here, never named a second time. */
    if (!mg_gem_slot_list_push(
            slots,
            (mg_gem_slot) {.name = name, .ptr_address = ptr_address, .default_fn = *ptr_address})) {
        MG_WARN("Out of memory registering gem slot '%s'.", name);
    }
}

mg_gem_slot_table mg_gem_slots(void) {
    const mg_gem_slot_table table = {(const mg_gem_slot*)g_slots.items, g_slots.count};
    return table;
}

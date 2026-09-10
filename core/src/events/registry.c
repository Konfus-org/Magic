/**
 * @file registry.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Event registration. Implements magic/events/registry.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/events/registry.h"
#include "magic/debug/logging.h"
#include "magic/memory/copy.h"

/* The registry in use: this module's own pointer, so registration and firing
 * need no knowledge of who owns the registry -- the app aims this at its own
 * as it opens and nothing here ever asks whose it was. NULL, the state
 * before open and after close, makes registering loud and firing quiet. */
static mg_event_registry* g_events = NULL;

/**
 * @brief The registration list evt_code's listeners live on, or NULL for a
 * code past the table. No fill here: a lookup that finds nothing means
 * nobody registered, which reads and fires need answered quietly.
 */
static mg_event_registration_list* mg_find_registrations(const u32 evt_code) {
    if (evt_code == MG_EVENT_CODE_ANY) {
        return &g_events->any;
    }
    return evt_code < g_events->codes.count
               ? mg_event_code_table_at(&g_events->codes, (ind)evt_code)
               : NULL;
}

void mg_use_event_registry(mg_event_registry* events) {
    g_events = events;
}

void mg_event_registry_clear(mg_event_registry* events) {
    MG_ASSERT_RETURN_IF(events != NULL, "events must not be NULL");
    mg_event_registration_list* registrations;
    mg_foreach(registrations, &events->codes) {
        mg_event_registration_list_free(registrations);
    }
    mg_buffer_clear(&events->codes);
    mg_event_registration_list_free(&events->any);
    mg_addr_list_free(&events->senders);
    mg_event_list_free(&events->queue);
}

void mg_register_event_listener(
    const u32 evt_code,
    void* listener,
    const mg_event_handler handler) {
    MG_ASSERT_RETURN_IF(g_events != NULL, "no event registry is in use");

    /* The per-code lists arrive here, on the first registration: the table's
     * storage is static and already sized, and a constant expression cannot
     * spell the lists, so filling is this push over already-zeroed slots.
     * After a clear the same push refills. */
    while (!mg_buffer_full(&g_events->codes)) {
        mg_event_code_table_push(&g_events->codes, mg_event_registration_list_construct());
    }

    mg_event_registration_list* registrations = mg_find_registrations(evt_code);
    MG_ASSERT_RETURN_IF(registrations != NULL, "no slot for evt_code: out of range");
    mg_event_registration_list_push(
        registrations,
        (mg_event_registration) {.listener = listener, .handler = handler});
}

void mg_unregister_event_listener(
    const u32 evt_code,
    const void* listener,
    const mg_event_handler handler) {
    if (g_events == NULL) {
        return;
    }

    mg_event_registration_list* registrations = mg_find_registrations(evt_code);
    if (registrations == NULL) {
        return;
    }

    /* Compared field by field, not through index_of, which compares bytes
     * padding included and owes nothing about a struct's gaps. */
    ind index = 0;
    mg_event_registration* registration;
    mg_foreach(registration, registrations) {
        if (registration->listener == listener && registration->handler == handler) {
            mg_event_registration_list_remove(registrations, index);
            return;
        }
        index++;
    }
}

b8 mg_fire_event(const void* sender, const mg_event* evt) {
    MG_ASSERT_RETURN_VAL_IF(evt != NULL, FALSE, "evt must not be NULL");

    // No registry in use is nobody listening, not an error.
    if (g_events == NULL) {
        return FALSE;
    }

    /* ANY listeners hear first, ahead of the code's own: a handled event ends
     * the fire, and an every-event listener should miss nothing. */
    mg_event_registration* registration;
    mg_foreach(registration, &g_events->any) {
        if (registration->handler(registration->listener, sender, evt)) {
            return TRUE;
        }
    }

    const mg_event_registration_list* registrations = mg_find_registrations(evt->code);
    if (registrations == NULL) {
        return FALSE;
    }

    mg_foreach(registration, registrations) {
        if (registration->handler(registration->listener, sender, evt)) {
            return TRUE;
        }
    }

    return FALSE;
}

void mg_queue_event(const void* sender, const mg_event* evt) {
    MG_ASSERT_RETURN_IF(evt != NULL, "evt must not be NULL");
    MG_ASSERT_RETURN_IF(g_events != NULL, "no event registry is in use to queue into");
    mg_addr_list_push(&g_events->senders, (addr)sender);
    mg_event_list_push(&g_events->queue, mg_copy(evt));
}

void mg_process_event_queue(void) {
    if (g_events == NULL) {
        return;
    }
    while (!mg_buffer_empty(&g_events->queue)) {
        const mg_event evt = mg_event_list_pop_front(&g_events->queue);
        const addr sender = mg_addr_list_pop_front(&g_events->senders);
        mg_fire_event((const void*)sender, &evt);
    }
}

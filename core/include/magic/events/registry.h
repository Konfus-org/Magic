/**
 * @file registry.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The event registry: who is listening, per code and for every code,
 * and the queue of events waiting for the next pump. The functions here work
 * on the registry in use -- a pointer this module keeps, which the app aims
 * at its own registry as it opens -- so a call site fires or queues with no
 * registry in hand, and the module never has to know the app.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "event.h"
#include "magic/collections/array.h"
#include "magic/collections/list.h"
#include "magic/export.h"

/**
 * @brief Hears an event it was registered for.
 * @param listener Whoever registered the handler, handed back as given: the
 * handler's way to its own state, since a bare function carries none.
 * @param sender Whoever fired the event; informational, may be NULL.
 * @param evt The event, never NULL.
 * @return True when the event was handled, which stops the fire.
 */
typedef b8 (*mg_event_handler)(void* listener, const void* sender, const mg_event* evt);

/** @brief One registration: who is listening, and the handler that hears. */
typedef struct mg_event_registration {
    void* listener;
    mg_event_handler handler;
} mg_event_registration;

MG_DEFINE_LIST(mg_event_registration, NULL);
MG_DEFINE_NAMED_ARRAY(mg_event_registration_list, mg_event_code_table, NULL);

/**
 * @brief Everything a registry keeps about events: the listeners, and the
 * queue. The app owns the one instance as a field; construct it, and clear
 * it when its owner closes -- there is no init.
 */
typedef struct mg_event_registry {
    mg_event_code_table codes; /**< One registration list per engine code, indexed by it;
                                * fills itself on the first registration. */
    mg_event_registration_list any; /**< MG_EVENT_CODE_ANY's listeners. ANY is U32_MAX,
                                     * deliberately outside the table's range so user-minted
                                     * codes past MG_EVENT_CODE_COUNT can never collide with
                                     * it; no table slot answers to it, so its listeners
                                     * keep their own list. */
    mg_addr_list senders; /**< Queued senders, one per queued event, in queue order. */
    mg_event_list queue; /**< Events waiting for mg_process_event_queue. */
} mg_event_registry;

/**
 * @brief An event registry as a constant expression, which a file-scope
 * registry like the app's requires: the code table over static storage in
 * the manner of mg_buffer_construct, everything else an empty list. The
 * per-code lists a constant expression cannot spell arrive on the first
 * registration, so a constructed registry is ready as it stands.
 */
#define mg_event_registry_construct()                                                              \
    {                                                                                              \
        .codes = mg_array_construct(mg_event_registration_list, MG_EVENT_CODE_COUNT),              \
        .any = mg_list_construct(mg_event_registration),                                           \
        .senders = mg_list_construct(addr),                                                        \
        .queue = mg_list_construct(mg_event),                                                      \
    }

/**
 * @brief Points the registration and fire functions at the registry they
 * work on, replacing whatever registry held the post; NULL stands the module
 * down. The app aims this at its own registry as it opens and back at NULL
 * as it closes. Borrowed until replaced.
 */
MG_EXPORT void mg_use_event_registry(mg_event_registry* events);

/** @brief Clears a registry: every registration and everything queued. */
MG_EXPORT void mg_event_registry_clear(mg_event_registry* events);

/**
 * @brief Registers a handler to hear evt_code's events, on the registry in use.
 * @param evt_code The type of event to register.
 * @param listener Handed to the handler on every fire, as given; usually the
 * object listening, for the handler to act on. Borrowed until unregistered.
 * @param handler The handler to register.
 */
MG_EXPORT void mg_register_event_listener(u32 evt_code, void* listener, mg_event_handler handler);

/**
 * @brief Unregisters one earlier registration: the pair of listener and
 * handler, so two listeners sharing a handler unhook separately.
 * @param evt_code The type of event to unregister.
 * @param listener The listener the handler was registered with.
 * @param handler The handler to unregister.
 */
MG_EXPORT void mg_unregister_event_listener(
    u32 evt_code,
    const void* listener,
    mg_event_handler handler);

/**
 * @brief Fires an event to all registered listeners.
 * @param sender The sender of the event.
 * @param evt The event to fire.
 * @return b8 True if the event was successfully handled, false otherwise.
 */
MG_EXPORT b8 mg_fire_event(const void* sender, const mg_event* evt);

/**
 * @brief Queues an event to be fired to all registered listeners on next poll.
 * @param sender The sender of the event.
 * @param evt The event to fire.
 */
MG_EXPORT void mg_queue_event(const void* sender, const mg_event* evt);

/**
 * @brief Processes the event queue, firing all queued events to all
 * registered listeners in the order they were queued.
 */
MG_EXPORT void mg_process_event_queue(void);

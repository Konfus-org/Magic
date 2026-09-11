/**
 * @file sdl_events.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Events: SDL's queue drained and translated into the engine's codes,
 * one event per poll, each carrying the payload its code promises in event.h.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/memory/box.h"
#include "sdl_platform.h"
#include <SDL3/SDL.h>

/**
 * @brief How many payloads live at once. The engine drains its queue every
 * frame, so this only has to outlast one frame's worth of translated events;
 * a frame somehow queueing more would overwrite the oldest payload still
 * waiting.
 */
#define SDL_EVENT_PAYLOAD_COUNT 256

/** @brief One translated event's payload, whichever shape its code takes. */
typedef union sdl_event_payload {
    mg_window_resize_event resize;
    mg_input_device_event device;
    mg_input_press_event press;
    mg_mouse_move_event move;
} sdl_event_payload;

/** @brief The payload ring: boxed events point into it, so it outlives the poll. */
static sdl_event_payload g_payloads[SDL_EVENT_PAYLOAD_COUNT];
static len g_next_payload = 0;

/**
 * @brief The magic event an SDL event maps to, or FALSE for one that maps to
 * nothing. A translation's payload lands in the ring above and rides the
 * event as a box; codes with nothing to carry ride with MG_NIL_BOX.
 * @param event The SDL event to translate.
 * @param out_event Receives the translation when there is one.
 * @return True when the event translated.
 */
static b8 sdl_translate_event(const SDL_Event* event, mg_event* out_event) {
    mg_event_code type = MG_EVENT_CODE_NONE;
    mg_box data = MG_NIL_BOX;
    sdl_event_payload* payload = &g_payloads[g_next_payload];

    switch (event->type) {
        case SDL_EVENT_QUIT:
        case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
            type = MG_EVENT_CODE_APP_QUITING;
            break;
        case SDL_EVENT_WINDOW_SHOWN:
            type = MG_EVENT_CODE_WINDOW_OPENED;
            break;
        case SDL_EVENT_WINDOW_DESTROYED:
            type = MG_EVENT_CODE_WINDOW_CLOSED;
            break;
        case SDL_EVENT_WINDOW_MINIMIZED:
            type = MG_EVENT_CODE_WINDOW_MINIMIZED;
            break;
        case SDL_EVENT_WINDOW_RESIZED:
            type = MG_EVENT_CODE_WINDOW_RESIZED;
            payload->resize = (mg_window_resize_event) {
                .size = {
                    .width = (u32)event->window.data1,
                    .height = (u32)event->window.data2}};
            data = mg_boxup(&payload->resize);
            break;
        case SDL_EVENT_KEYBOARD_ADDED:
        case SDL_EVENT_KEYBOARD_REMOVED:
            type = event->type == SDL_EVENT_KEYBOARD_ADDED
                ? MG_EVENT_CODE_INPUT_DEVICE_CONNECTED
                : MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED;
            payload->device = (mg_input_device_event) {.kind = MG_INPUT_DEVICE_KEYBOARD};
            data = mg_boxup(&payload->device);
            break;
        case SDL_EVENT_MOUSE_ADDED:
        case SDL_EVENT_MOUSE_REMOVED:
            type = event->type == SDL_EVENT_MOUSE_ADDED
                ? MG_EVENT_CODE_INPUT_DEVICE_CONNECTED
                : MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED;
            payload->device = (mg_input_device_event) {.kind = MG_INPUT_DEVICE_MOUSE};
            data = mg_boxup(&payload->device);
            break;
        case SDL_EVENT_KEY_DOWN:
        case SDL_EVENT_KEY_UP:
            type = event->type == SDL_EVENT_KEY_DOWN ? MG_EVENT_CODE_INPUT_PRESSED
                                                     : MG_EVENT_CODE_INPUT_RELEASED;
            payload->press = (mg_input_press_event) {
                .key = sdl_key_from_scancode(event->key.scancode),
                .button = MG_BUTTON_NONE};
            data = mg_boxup(&payload->press);
            break;
        case SDL_EVENT_MOUSE_BUTTON_DOWN:
        case SDL_EVENT_MOUSE_BUTTON_UP:
            type = event->type == SDL_EVENT_MOUSE_BUTTON_DOWN ? MG_EVENT_CODE_INPUT_PRESSED
                                                              : MG_EVENT_CODE_INPUT_RELEASED;
            payload->press = (mg_input_press_event) {
                .key = MG_KEY_NONE,
                .button = sdl_button_from_mouse(event->button.button)};
            data = mg_boxup(&payload->press);
            break;
        case SDL_EVENT_MOUSE_MOTION:
            type = MG_EVENT_CODE_INPUT_MOUSE_MOVED;
            payload->move = (mg_mouse_move_event) {
                .x = event->motion.x,
                .y = event->motion.y,
                .delta_x = event->motion.xrel,
                .delta_y = event->motion.yrel};
            data = mg_boxup(&payload->move);
            break;
        default:
            return FALSE;
    }

    /* Only a taken slot moves the ring, so skipped events burn none. */
    if (data.data != NULL) {
        g_next_payload = (g_next_payload + 1) % SDL_EVENT_PAYLOAD_COUNT;
    }

    out_event->code = (u32)type;
    out_event->data = data;
    return TRUE;
}

/**
 * @brief gs_platform_poll_event via SDL: the next SDL event that maps to
 * an engine event, translated. SDL events that map to nothing are consumed
 * and skipped; wheel events are among them, but their steps feed the input
 * piece before they go. A quit comes out like the rest and lowers the running
 * flag, so the pump reports it the same frame the engine hears the event. The
 * engine does the queueing -- this gem's own event registry is not the
 * engine's, so queueing here would fill a queue nobody drains.
 * @param out_event Receives the translated event when one came out.
 * @return TRUE when out_event holds an event; FALSE when none are pending.
 */
b8 sdl_poll_event(mg_event* out_event) {
    SDL_Event event;
    mg_event translated;
    while (SDL_PollEvent(&event)) {
        if (event.type == SDL_EVENT_MOUSE_WHEEL) {
            sdl_note_wheel(event.wheel.x, event.wheel.y);
        }
        if (!sdl_translate_event(&event, &translated)) {
            continue;
        }
        if (translated.code == MG_EVENT_CODE_APP_QUITING) {
            sdl_stop_running();
        }
        if (out_event != NULL) {
            *out_event = translated;
        }
        return TRUE;
    }
    return FALSE;
}

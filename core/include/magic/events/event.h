/**
 * @file event.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Event definition.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/list.h"
#include "magic/enums.h"
#include "magic/memory/box.h"
#include "magic/platform/input_codes.h"

/** @brief Register to receive all types of events. */
#define MG_EVENT_CODE_ANY U32_MAX

/** @brief Every event code, for the mg_event_code mint below. */
#define MG_EVENT_CODE_VALUES_(X)                                                                   \
    X(MG_EVENT_CODE_NONE)                                                                          \
    X(MG_EVENT_CODE_APP_INITIALIZED)                                                               \
    X(MG_EVENT_CODE_APP_REQUEST_EXIT)                                                              \
    X(MG_EVENT_CODE_APP_QUITING)                                                                   \
    X(MG_EVENT_CODE_WINDOW_OPENED)                                                                 \
    X(MG_EVENT_CODE_WINDOW_CLOSED)                                                                 \
    X(MG_EVENT_CODE_WINDOW_MINIMIZED)                                                              \
    X(MG_EVENT_CODE_WINDOW_RESIZED)                                                                \
    X(MG_EVENT_CODE_INPUT_DEVICE_CONNECTED)                                                        \
    X(MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED)                                                     \
    X(MG_EVENT_CODE_INPUT_PRESSED)                                                                 \
    X(MG_EVENT_CODE_INPUT_RELEASED)                                                                \
    X(MG_EVENT_CODE_INPUT_MOUSE_MOVED)                                                             \
    X(MG_EVENT_CODE_COMMAND_SENT)

/** @brief The type of event, for a custom event make a new enum and have its first element = MG_EVENT_CODE_COUNT */
MG_DEFINE_ENUM(mg_event_code, MG_EVENT_CODE_COUNT, MG_EVENT_CODE_VALUES_);

/* The payloads the platform codes carry: boxed onto the event's data with
 * mg_boxup, read back with mg_unbox against the type the code promises. A
 * code with no payload type here carries MG_NIL_BOX. */

/** @brief What MG_EVENT_CODE_WINDOW_RESIZED carries. */
typedef struct mg_window_resize_event {
    s32 size; /**< The new window size. */
} mg_window_resize_event;

/**
 * @brief What MG_EVENT_CODE_INPUT_DEVICE_CONNECTED and _DISCONNECTED carry.
 */
typedef struct mg_input_device_event {
    mg_input_device_kind kind; /**< The kind of device that came or went. */
} mg_input_device_event;

/**
 * @brief What MG_EVENT_CODE_INPUT_PRESSED and _RELEASED carry: the control
 * that changed. Exactly one of key and button names it; the other holds its
 * NONE.
 */
typedef struct mg_input_press_event {
    mg_key_code key; /**< The key, or MG_KEY_NONE for a button press. */
    mg_button_code button; /**< The button, or MG_BUTTON_NONE for a key press. */
} mg_input_press_event;

/** @brief What MG_EVENT_CODE_INPUT_MOUSE_MOVED carries. */
typedef struct mg_mouse_move_event {
    f32 x; /**< Where the mouse now sits, in window coordinates. */
    f32 y;
    f32 delta_x; /**< How far it moved since the previous report. */
    f32 delta_y;
} mg_mouse_move_event;

/** @brief One event, and whatever its type carries with it. */
typedef struct mg_event {
    u32 code;
    mg_box data;
} mg_event;

MG_DEFINE_LIST(mg_event, NULL);

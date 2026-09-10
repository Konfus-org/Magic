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
#include "magic/memory/box.h"

/** @brief Register to receive all types of events. */
#define MG_EVENT_CODE_ANY U32_MAX

/** @brief The type of event, for a custom event make a new enum and have its first element = MG_EVENT_CODE_COUNT */
typedef enum mg_event_code {
    MG_EVENT_CODE_NONE,
    MG_EVENT_CODE_APP_INITIALIZED,
    MG_EVENT_CODE_APP_REQUEST_EXIT,
    MG_EVENT_CODE_APP_QUITING,
    MG_EVENT_CODE_WINDOW_OPENED,
    MG_EVENT_CODE_WINDOW_CLOSED,
    MG_EVENT_CODE_WINDOW_MINIMIZED,
    MG_EVENT_CODE_WINDOW_RESIZED,
    MG_EVENT_CODE_INPUT_DEVICE_CONNECTED,
    MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED,
    MG_EVENT_CODE_INPUT_PRESSED,
    MG_EVENT_CODE_INPUT_RELEASED,
    MG_EVENT_CODE_INPUT_MOUSE_MOVED,
    MG_EVENT_CODE_COMMAND_SENT,
    MG_EVENT_CODE_COUNT,
} mg_event_code;

/** @brief One event, and whatever its type carries with it. */
typedef struct mg_event {
    u32 code;
    mg_box data;
} mg_event;

MG_DEFINE_LIST(mg_event, NULL);

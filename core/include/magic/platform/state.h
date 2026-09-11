/**
 * @file state.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The state the platform backend reports through its lifecycle slots,
 * which live in magic/platform/slots.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/string.h"
#include "magic/memory/box.h"
#include "magic/platform/input_codes.h"
#include "magic/primitives.h"

/** @brief The state the platform backend reports through its lifecycle slots. */
typedef struct mg_input_state {
    mg_input_array
        buttons; /**< The buttons (mouse or gamepad), controller-major: MG_BUTTON_COUNT
                     values per controller, MG_MAX_CONTROLLERS deep, indexed with
                     MG_BUTTON_INDEX. The mouse's buttons sit in controller 0's row. */
    mg_input_array keyboard; /**< The state of the keyboard */
    mg_input_array
        analog; /**< The analog inputs (-1 to 1: mouse delta, mouse wheel, gamepad
                    sticks and triggers), controller-major like buttons, indexed
                    with MG_AXIS_INDEX. The mouse's axes sit in controller 0's row. */
} mg_input_state;

/** @brief The state of the window provided by the platform. */
typedef struct mg_window_state {
    mg_str_view title;
    s32 window_size;
    u8 window_mode;
} mg_window_state;

/** @brief The state of the platform. */
typedef struct mg_platform_state {
    mg_str_view name; /**< The name of the platform backend */
    mg_window_state window;
    mg_input_state input;
    mg_box data; /**< Platform-specific data */
} mg_platform_state;

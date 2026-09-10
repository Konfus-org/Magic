/**
 * @file input_codes.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Input codes for a platform (mouse, keyboard, and gamepads).
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once

/** @brief Mouse or gamepad button. */
typedef enum mg_button_code {
    MG_BUTTON_NONE,
    MG_MOUSE_BUTTON_LEFT,
    MG_MOUSE_BUTTON_MIDDLE,
    MG_MOUSE_BUTTON_RIGHT,
    MG_MOUSE_BUTTON_X1, /**< First extra button, usually browse-back. */
    MG_MOUSE_BUTTON_X2, /**< Second extra button, usually browse-forward. */

    MG_GAMEPAD_BUTTON_SOUTH, /**< Bottom face button: A on Xbox, Cross on PlayStation. */
    MG_GAMEPAD_BUTTON_EAST, /**< Right face button: B on Xbox, Circle on PlayStation. */
    MG_GAMEPAD_BUTTON_WEST, /**< Left face button: X on Xbox, Square on PlayStation. */
    MG_GAMEPAD_BUTTON_NORTH, /**< Top face button: Y on Xbox, Triangle on PlayStation. */
    MG_GAMEPAD_BUTTON_BACK, /**< Back, View or Select, by the pad's name for it. */
    MG_GAMEPAD_BUTTON_GUIDE, /**< The system button: the Xbox or PS logo. */
    MG_GAMEPAD_BUTTON_START, /**< Start, Menu or Options, by the pad's name for it. */
    MG_GAMEPAD_BUTTON_LEFT_STICK, /**< Clicking the left stick in. */
    MG_GAMEPAD_BUTTON_RIGHT_STICK, /**< Clicking the right stick in. */
    MG_GAMEPAD_BUTTON_LEFT_SHOULDER,
    MG_GAMEPAD_BUTTON_RIGHT_SHOULDER,
    MG_GAMEPAD_BUTTON_DPAD_UP,
    MG_GAMEPAD_BUTTON_DPAD_DOWN,
    MG_GAMEPAD_BUTTON_DPAD_LEFT,
    MG_GAMEPAD_BUTTON_DPAD_RIGHT,

    MG_BUTTON_COUNT
} mg_button_code;

/**
 * @brief Codes name the physical key by its place on a US layout, whatever the label under the user's
 * layout, so WASD stays WASD everywhere.
 */
typedef enum mg_key_code {
    MG_KEY_NONE,

    MG_KEY_A,
    MG_KEY_B,
    MG_KEY_C,
    MG_KEY_D,
    MG_KEY_E,
    MG_KEY_F,
    MG_KEY_G,
    MG_KEY_H,
    MG_KEY_I,
    MG_KEY_J,
    MG_KEY_K,
    MG_KEY_L,
    MG_KEY_M,
    MG_KEY_N,
    MG_KEY_O,
    MG_KEY_P,
    MG_KEY_Q,
    MG_KEY_R,
    MG_KEY_S,
    MG_KEY_T,
    MG_KEY_U,
    MG_KEY_V,
    MG_KEY_W,
    MG_KEY_X,
    MG_KEY_Y,
    MG_KEY_Z,

    MG_KEY_0,
    MG_KEY_1,
    MG_KEY_2,
    MG_KEY_3,
    MG_KEY_4,
    MG_KEY_5,
    MG_KEY_6,
    MG_KEY_7,
    MG_KEY_8,
    MG_KEY_9,

    MG_KEY_F1,
    MG_KEY_F2,
    MG_KEY_F3,
    MG_KEY_F4,
    MG_KEY_F5,
    MG_KEY_F6,
    MG_KEY_F7,
    MG_KEY_F8,
    MG_KEY_F9,
    MG_KEY_F10,
    MG_KEY_F11,
    MG_KEY_F12,

    MG_KEY_ESCAPE,
    MG_KEY_TAB,
    MG_KEY_CAPS_LOCK,
    MG_KEY_SPACE,
    MG_KEY_ENTER,
    MG_KEY_BACKSPACE,

    MG_KEY_LEFT_SHIFT,
    MG_KEY_RIGHT_SHIFT,
    MG_KEY_LEFT_CONTROL,
    MG_KEY_RIGHT_CONTROL,
    MG_KEY_LEFT_ALT,
    MG_KEY_RIGHT_ALT,
    MG_KEY_LEFT_SUPER, /**< The left OS key: Windows or Command. */
    MG_KEY_RIGHT_SUPER, /**< The right OS key: Windows or Command. */

    MG_KEY_GRAVE, /**< ` and ~, above tab. */
    MG_KEY_MINUS,
    MG_KEY_EQUALS,
    MG_KEY_LEFT_BRACKET,
    MG_KEY_RIGHT_BRACKET,
    MG_KEY_BACKSLASH,
    MG_KEY_SEMICOLON,
    MG_KEY_APOSTROPHE,
    MG_KEY_COMMA,
    MG_KEY_PERIOD,
    MG_KEY_SLASH,

    MG_KEY_INSERT,
    MG_KEY_DELETE,
    MG_KEY_HOME,
    MG_KEY_END,
    MG_KEY_PAGE_UP,
    MG_KEY_PAGE_DOWN,
    MG_KEY_UP,
    MG_KEY_DOWN,
    MG_KEY_LEFT,
    MG_KEY_RIGHT,

    MG_KEY_PRINT_SCREEN,
    MG_KEY_SCROLL_LOCK,
    MG_KEY_PAUSE,

    MG_KEY_NUM_LOCK,
    MG_KEY_NUMPAD_0,
    MG_KEY_NUMPAD_1,
    MG_KEY_NUMPAD_2,
    MG_KEY_NUMPAD_3,
    MG_KEY_NUMPAD_4,
    MG_KEY_NUMPAD_5,
    MG_KEY_NUMPAD_6,
    MG_KEY_NUMPAD_7,
    MG_KEY_NUMPAD_8,
    MG_KEY_NUMPAD_9,
    MG_KEY_NUMPAD_DIVIDE,
    MG_KEY_NUMPAD_MULTIPLY,
    MG_KEY_NUMPAD_MINUS,
    MG_KEY_NUMPAD_PLUS,
    MG_KEY_NUMPAD_ENTER,
    MG_KEY_NUMPAD_PERIOD,

    MG_KEY_COUNT
} mg_key_code;

/**
 * @brief Each code names a whole control reporting up to two values at once, not a
 * single direction: a stick or the mouse moves in x and y together, a
 * trigger only fills x. Sticks run -1 to 1 per direction and triggers 0 to 1;
 * the mouse reports movement in pixels and the wheel in scroll steps.
 */
typedef enum mg_axis_code {
    MG_AXIS_NONE,
    MG_AXIS_MOUSE, /**< Mouse movement since the last report. */
    MG_AXIS_MOUSE_WHEEL, /**< Scrolling: y is the usual wheel, x is sideways. */
    MG_AXIS_LEFT_STICK,
    MG_AXIS_RIGHT_STICK,
    MG_AXIS_LEFT_TRIGGER,
    MG_AXIS_RIGHT_TRIGGER,
    MG_AXIS_COUNT
} mg_axis_code;

MG_DEFINE_NAMED_ARRAY(f32, mg_input_array, NULL);

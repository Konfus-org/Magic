/**
 * @file input_codes.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Input codes for a platform (mouse, keyboard, and gamepads).
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/array.h"
#include "magic/enums.h"

/**
 * @brief How many controllers the input state tracks. The mouse is not one
 * of them: it has its own endpoints, which read slot 0, since there is only
 * ever the one mouse.
 */
#define MG_MAX_CONTROLLERS 8

/** @brief Every device kind, for the mg_input_device_kind mint below. */
#define MG_INPUT_DEVICE_VALUES_(X)                                                                 \
    X(MG_INPUT_DEVICE_NONE)                                                                        \
    X(MG_INPUT_DEVICE_KEYBOARD)                                                                    \
    X(MG_INPUT_DEVICE_MOUSE)                                                                       \
    X(MG_INPUT_DEVICE_GAMEPAD)

/** @brief The kinds of device an input device event can name. */
MG_DEFINE_ENUM(mg_input_device_kind, MG_INPUT_DEVICE_COUNT, MG_INPUT_DEVICE_VALUES_);

/** @brief Every button code, for the mg_button_code mint below. */
#define MG_BUTTON_CODE_VALUES_(X)                                                                  \
    X(MG_BUTTON_NONE)                                                                              \
    X(MG_MOUSE_BUTTON_LEFT)                                                                        \
    X(MG_MOUSE_BUTTON_MIDDLE)                                                                      \
    X(MG_MOUSE_BUTTON_RIGHT)                                                                       \
    X(MG_MOUSE_BUTTON_X1) /**< First extra button, usually browse-back. */                         \
    X(MG_MOUSE_BUTTON_X2) /**< Second extra button, usually browse-forward. */                     \
                                                                                                   \
    X(MG_GAMEPAD_BUTTON_SOUTH) /**< Bottom face button: A on Xbox, Cross on PlayStation. */        \
    X(MG_GAMEPAD_BUTTON_EAST) /**< Right face button: B on Xbox, Circle on PlayStation. */         \
    X(MG_GAMEPAD_BUTTON_WEST) /**< Left face button: X on Xbox, Square on PlayStation. */          \
    X(MG_GAMEPAD_BUTTON_NORTH) /**< Top face button: Y on Xbox, Triangle on PlayStation. */        \
    X(MG_GAMEPAD_BUTTON_BACK) /**< Back, View or Select, by the pad's name for it. */              \
    X(MG_GAMEPAD_BUTTON_GUIDE) /**< The system button: the Xbox or PS logo. */                     \
    X(MG_GAMEPAD_BUTTON_START) /**< Start, Menu or Options, by the pad's name for it. */           \
    X(MG_GAMEPAD_BUTTON_LEFT_STICK) /**< Clicking the left stick in. */                            \
    X(MG_GAMEPAD_BUTTON_RIGHT_STICK) /**< Clicking the right stick in. */                          \
    X(MG_GAMEPAD_BUTTON_LEFT_SHOULDER)                                                             \
    X(MG_GAMEPAD_BUTTON_RIGHT_SHOULDER)                                                            \
    X(MG_GAMEPAD_BUTTON_DPAD_UP)                                                                   \
    X(MG_GAMEPAD_BUTTON_DPAD_DOWN)                                                                 \
    X(MG_GAMEPAD_BUTTON_DPAD_LEFT)                                                                 \
    X(MG_GAMEPAD_BUTTON_DPAD_RIGHT)

/** @brief Mouse or gamepad button. */
MG_DEFINE_ENUM(mg_button_code, MG_BUTTON_COUNT, MG_BUTTON_CODE_VALUES_);

/**
 * @brief The slot button occupies for controller in a buttons array laid out
 * controller-major: MG_BUTTON_COUNT values per controller, MG_MAX_CONTROLLERS
 * controllers deep. The mouse's buttons sit in controller 0's row.
 */
#define MG_BUTTON_INDEX(controller, button)                                                        \
    ((ind)(controller) * (ind)MG_BUTTON_COUNT + (ind)(button))

/** @brief Every key code, for the mg_key_code mint below. */
#define MG_KEY_CODE_VALUES_(X)                                                                     \
    X(MG_KEY_NONE)                                                                                 \
                                                                                                   \
    X(MG_KEY_A)                                                                                    \
    X(MG_KEY_B)                                                                                    \
    X(MG_KEY_C)                                                                                    \
    X(MG_KEY_D)                                                                                    \
    X(MG_KEY_E)                                                                                    \
    X(MG_KEY_F)                                                                                    \
    X(MG_KEY_G)                                                                                    \
    X(MG_KEY_H)                                                                                    \
    X(MG_KEY_I)                                                                                    \
    X(MG_KEY_J)                                                                                    \
    X(MG_KEY_K)                                                                                    \
    X(MG_KEY_L)                                                                                    \
    X(MG_KEY_M)                                                                                    \
    X(MG_KEY_N)                                                                                    \
    X(MG_KEY_O)                                                                                    \
    X(MG_KEY_P)                                                                                    \
    X(MG_KEY_Q)                                                                                    \
    X(MG_KEY_R)                                                                                    \
    X(MG_KEY_S)                                                                                    \
    X(MG_KEY_T)                                                                                    \
    X(MG_KEY_U)                                                                                    \
    X(MG_KEY_V)                                                                                    \
    X(MG_KEY_W)                                                                                    \
    X(MG_KEY_X)                                                                                    \
    X(MG_KEY_Y)                                                                                    \
    X(MG_KEY_Z)                                                                                    \
                                                                                                   \
    X(MG_KEY_0)                                                                                    \
    X(MG_KEY_1)                                                                                    \
    X(MG_KEY_2)                                                                                    \
    X(MG_KEY_3)                                                                                    \
    X(MG_KEY_4)                                                                                    \
    X(MG_KEY_5)                                                                                    \
    X(MG_KEY_6)                                                                                    \
    X(MG_KEY_7)                                                                                    \
    X(MG_KEY_8)                                                                                    \
    X(MG_KEY_9)                                                                                    \
                                                                                                   \
    X(MG_KEY_F1)                                                                                   \
    X(MG_KEY_F2)                                                                                   \
    X(MG_KEY_F3)                                                                                   \
    X(MG_KEY_F4)                                                                                   \
    X(MG_KEY_F5)                                                                                   \
    X(MG_KEY_F6)                                                                                   \
    X(MG_KEY_F7)                                                                                   \
    X(MG_KEY_F8)                                                                                   \
    X(MG_KEY_F9)                                                                                   \
    X(MG_KEY_F10)                                                                                  \
    X(MG_KEY_F11)                                                                                  \
    X(MG_KEY_F12)                                                                                  \
                                                                                                   \
    X(MG_KEY_ESCAPE)                                                                               \
    X(MG_KEY_TAB)                                                                                  \
    X(MG_KEY_CAPS_LOCK)                                                                            \
    X(MG_KEY_SPACE)                                                                                \
    X(MG_KEY_ENTER)                                                                                \
    X(MG_KEY_BACKSPACE)                                                                            \
                                                                                                   \
    X(MG_KEY_LEFT_SHIFT)                                                                           \
    X(MG_KEY_RIGHT_SHIFT)                                                                          \
    X(MG_KEY_LEFT_CONTROL)                                                                         \
    X(MG_KEY_RIGHT_CONTROL)                                                                        \
    X(MG_KEY_LEFT_ALT)                                                                             \
    X(MG_KEY_RIGHT_ALT)                                                                            \
    X(MG_KEY_LEFT_SUPER) /**< The left OS key: Windows or Command. */                              \
    X(MG_KEY_RIGHT_SUPER) /**< The right OS key: Windows or Command. */                            \
                                                                                                   \
    X(MG_KEY_GRAVE) /**< ` and ~, above tab. */                                                    \
    X(MG_KEY_MINUS)                                                                                \
    X(MG_KEY_EQUALS)                                                                               \
    X(MG_KEY_LEFT_BRACKET)                                                                         \
    X(MG_KEY_RIGHT_BRACKET)                                                                        \
    X(MG_KEY_BACKSLASH)                                                                            \
    X(MG_KEY_SEMICOLON)                                                                            \
    X(MG_KEY_APOSTROPHE)                                                                           \
    X(MG_KEY_COMMA)                                                                                \
    X(MG_KEY_PERIOD)                                                                               \
    X(MG_KEY_SLASH)                                                                                \
                                                                                                   \
    X(MG_KEY_INSERT)                                                                               \
    X(MG_KEY_DELETE)                                                                               \
    X(MG_KEY_HOME)                                                                                 \
    X(MG_KEY_END)                                                                                  \
    X(MG_KEY_PAGE_UP)                                                                              \
    X(MG_KEY_PAGE_DOWN)                                                                            \
    X(MG_KEY_UP)                                                                                   \
    X(MG_KEY_DOWN)                                                                                 \
    X(MG_KEY_LEFT)                                                                                 \
    X(MG_KEY_RIGHT)                                                                                \
                                                                                                   \
    X(MG_KEY_PRINT_SCREEN)                                                                         \
    X(MG_KEY_SCROLL_LOCK)                                                                          \
    X(MG_KEY_PAUSE)                                                                                \
                                                                                                   \
    X(MG_KEY_NUM_LOCK)                                                                             \
    X(MG_KEY_NUMPAD_0)                                                                             \
    X(MG_KEY_NUMPAD_1)                                                                             \
    X(MG_KEY_NUMPAD_2)                                                                             \
    X(MG_KEY_NUMPAD_3)                                                                             \
    X(MG_KEY_NUMPAD_4)                                                                             \
    X(MG_KEY_NUMPAD_5)                                                                             \
    X(MG_KEY_NUMPAD_6)                                                                             \
    X(MG_KEY_NUMPAD_7)                                                                             \
    X(MG_KEY_NUMPAD_8)                                                                             \
    X(MG_KEY_NUMPAD_9)                                                                             \
    X(MG_KEY_NUMPAD_DIVIDE)                                                                        \
    X(MG_KEY_NUMPAD_MULTIPLY)                                                                      \
    X(MG_KEY_NUMPAD_MINUS)                                                                         \
    X(MG_KEY_NUMPAD_PLUS)                                                                          \
    X(MG_KEY_NUMPAD_ENTER)                                                                         \
    X(MG_KEY_NUMPAD_PERIOD)

/**
 * @brief Codes name the physical key by its place on a US layout, whatever the label under the user's
 * layout, so WASD stays WASD everywhere.
 */
MG_DEFINE_ENUM(mg_key_code, MG_KEY_COUNT, MG_KEY_CODE_VALUES_);

/** @brief Every axis code, for the mg_axis_code mint below. */
#define MG_AXIS_CODE_VALUES_(X)                                                                    \
    X(MG_AXIS_NONE)                                                                                \
    X(MG_AXIS_MOUSE) /**< Mouse movement since the last report. */                                 \
    X(MG_AXIS_MOUSE_WHEEL) /**< Scrolling: y is the usual wheel, x is sideways. */                 \
    X(MG_AXIS_LEFT_STICK)                                                                          \
    X(MG_AXIS_RIGHT_STICK)                                                                         \
    X(MG_AXIS_LEFT_TRIGGER)                                                                        \
    X(MG_AXIS_RIGHT_TRIGGER)

/**
 * @brief Each code names a whole control reporting up to two values at once, not a
 * single direction: a stick or the mouse moves in x and y together, a
 * trigger only fills x. Sticks run -1 to 1 per direction and triggers 0 to 1;
 * the mouse reports movement in pixels and the wheel in scroll steps.
 */
MG_DEFINE_ENUM(mg_axis_code, MG_AXIS_COUNT, MG_AXIS_CODE_VALUES_);

/**
 * @brief The slot axis occupies for controller in an analog array laid out
 * controller-major, like MG_BUTTON_INDEX. The mouse's axes sit in
 * controller 0's row.
 */
#define MG_AXIS_INDEX(controller, axis) ((ind)(controller) * (ind)MG_AXIS_COUNT + (ind)(axis))

MG_DEFINE_NAMED_ARRAY(f32, mg_input_array, NULL);

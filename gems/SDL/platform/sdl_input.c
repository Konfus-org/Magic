/**
 * @file sdl_input.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Input: SDL's keyboard and mouse read into the engine's
 * mg_input_state, once per pump. Keys and buttons report 1 held and 0 not;
 * the mouse position packs into its one axis value with mg_pack_mouse_pos;
 * the wheel accumulates between pumps, fed by the window's event poll.
 * Gamepads wait on the gem growing SDL_INIT_GAMEPAD, so their codes stay 0.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/platform/input.h"
#include "magic/platform/input_codes.h"
#include "sdl_platform.h"
#include <SDL3/SDL.h>

/* The values behind the arrays gather hands out: static, so the state the
 * engine borrows outlives the call. One set only -- keeping was/is history
 * is the engine's input module's job, not the backend's. Buttons and axes
 * are controller-major, MG_MAX_CONTROLLERS deep; the mouse fills controller
 * 0's row. */
static f32 g_key_values[MG_KEY_COUNT];
static f32 g_button_values[MG_MAX_CONTROLLERS * MG_BUTTON_COUNT];
static f32 g_axis_values[MG_MAX_CONTROLLERS * MG_AXIS_COUNT];

/** @brief Wheel steps accumulated since the last gather. */
static f32 g_wheel_sideways = 0;
static f32 g_wheel_steps = 0;

/** @brief The key map read the other way; built from it on the first lookup. */
static mg_key_code g_key_for_scancode[SDL_SCANCODE_COUNT];
static b8 g_key_for_scancode_built = FALSE;

/**
 * @brief Where each engine key sits on a US layout, as SDL numbers it.
 * Both sides name the physical key by its US-layout place, so the map is one
 * scancode per code; the codes SDL has no scancode for stay UNKNOWN.
 */
static const SDL_Scancode g_scancode_for_key[MG_KEY_COUNT] = {
    [MG_KEY_NONE] = SDL_SCANCODE_UNKNOWN,

    [MG_KEY_A] = SDL_SCANCODE_A,
    [MG_KEY_B] = SDL_SCANCODE_B,
    [MG_KEY_C] = SDL_SCANCODE_C,
    [MG_KEY_D] = SDL_SCANCODE_D,
    [MG_KEY_E] = SDL_SCANCODE_E,
    [MG_KEY_F] = SDL_SCANCODE_F,
    [MG_KEY_G] = SDL_SCANCODE_G,
    [MG_KEY_H] = SDL_SCANCODE_H,
    [MG_KEY_I] = SDL_SCANCODE_I,
    [MG_KEY_J] = SDL_SCANCODE_J,
    [MG_KEY_K] = SDL_SCANCODE_K,
    [MG_KEY_L] = SDL_SCANCODE_L,
    [MG_KEY_M] = SDL_SCANCODE_M,
    [MG_KEY_N] = SDL_SCANCODE_N,
    [MG_KEY_O] = SDL_SCANCODE_O,
    [MG_KEY_P] = SDL_SCANCODE_P,
    [MG_KEY_Q] = SDL_SCANCODE_Q,
    [MG_KEY_R] = SDL_SCANCODE_R,
    [MG_KEY_S] = SDL_SCANCODE_S,
    [MG_KEY_T] = SDL_SCANCODE_T,
    [MG_KEY_U] = SDL_SCANCODE_U,
    [MG_KEY_V] = SDL_SCANCODE_V,
    [MG_KEY_W] = SDL_SCANCODE_W,
    [MG_KEY_X] = SDL_SCANCODE_X,
    [MG_KEY_Y] = SDL_SCANCODE_Y,
    [MG_KEY_Z] = SDL_SCANCODE_Z,

    [MG_KEY_0] = SDL_SCANCODE_0,
    [MG_KEY_1] = SDL_SCANCODE_1,
    [MG_KEY_2] = SDL_SCANCODE_2,
    [MG_KEY_3] = SDL_SCANCODE_3,
    [MG_KEY_4] = SDL_SCANCODE_4,
    [MG_KEY_5] = SDL_SCANCODE_5,
    [MG_KEY_6] = SDL_SCANCODE_6,
    [MG_KEY_7] = SDL_SCANCODE_7,
    [MG_KEY_8] = SDL_SCANCODE_8,
    [MG_KEY_9] = SDL_SCANCODE_9,

    [MG_KEY_F1] = SDL_SCANCODE_F1,
    [MG_KEY_F2] = SDL_SCANCODE_F2,
    [MG_KEY_F3] = SDL_SCANCODE_F3,
    [MG_KEY_F4] = SDL_SCANCODE_F4,
    [MG_KEY_F5] = SDL_SCANCODE_F5,
    [MG_KEY_F6] = SDL_SCANCODE_F6,
    [MG_KEY_F7] = SDL_SCANCODE_F7,
    [MG_KEY_F8] = SDL_SCANCODE_F8,
    [MG_KEY_F9] = SDL_SCANCODE_F9,
    [MG_KEY_F10] = SDL_SCANCODE_F10,
    [MG_KEY_F11] = SDL_SCANCODE_F11,
    [MG_KEY_F12] = SDL_SCANCODE_F12,

    [MG_KEY_ESCAPE] = SDL_SCANCODE_ESCAPE,
    [MG_KEY_TAB] = SDL_SCANCODE_TAB,
    [MG_KEY_CAPS_LOCK] = SDL_SCANCODE_CAPSLOCK,
    [MG_KEY_SPACE] = SDL_SCANCODE_SPACE,
    [MG_KEY_ENTER] = SDL_SCANCODE_RETURN,
    [MG_KEY_BACKSPACE] = SDL_SCANCODE_BACKSPACE,

    [MG_KEY_LEFT_SHIFT] = SDL_SCANCODE_LSHIFT,
    [MG_KEY_RIGHT_SHIFT] = SDL_SCANCODE_RSHIFT,
    [MG_KEY_LEFT_CONTROL] = SDL_SCANCODE_LCTRL,
    [MG_KEY_RIGHT_CONTROL] = SDL_SCANCODE_RCTRL,
    [MG_KEY_LEFT_ALT] = SDL_SCANCODE_LALT,
    [MG_KEY_RIGHT_ALT] = SDL_SCANCODE_RALT,
    [MG_KEY_LEFT_SUPER] = SDL_SCANCODE_LGUI,
    [MG_KEY_RIGHT_SUPER] = SDL_SCANCODE_RGUI,

    [MG_KEY_GRAVE] = SDL_SCANCODE_GRAVE,
    [MG_KEY_MINUS] = SDL_SCANCODE_MINUS,
    [MG_KEY_EQUALS] = SDL_SCANCODE_EQUALS,
    [MG_KEY_LEFT_BRACKET] = SDL_SCANCODE_LEFTBRACKET,
    [MG_KEY_RIGHT_BRACKET] = SDL_SCANCODE_RIGHTBRACKET,
    [MG_KEY_BACKSLASH] = SDL_SCANCODE_BACKSLASH,
    [MG_KEY_SEMICOLON] = SDL_SCANCODE_SEMICOLON,
    [MG_KEY_APOSTROPHE] = SDL_SCANCODE_APOSTROPHE,
    [MG_KEY_COMMA] = SDL_SCANCODE_COMMA,
    [MG_KEY_PERIOD] = SDL_SCANCODE_PERIOD,
    [MG_KEY_SLASH] = SDL_SCANCODE_SLASH,

    [MG_KEY_INSERT] = SDL_SCANCODE_INSERT,
    [MG_KEY_DELETE] = SDL_SCANCODE_DELETE,
    [MG_KEY_HOME] = SDL_SCANCODE_HOME,
    [MG_KEY_END] = SDL_SCANCODE_END,
    [MG_KEY_PAGE_UP] = SDL_SCANCODE_PAGEUP,
    [MG_KEY_PAGE_DOWN] = SDL_SCANCODE_PAGEDOWN,
    [MG_KEY_UP] = SDL_SCANCODE_UP,
    [MG_KEY_DOWN] = SDL_SCANCODE_DOWN,
    [MG_KEY_LEFT] = SDL_SCANCODE_LEFT,
    [MG_KEY_RIGHT] = SDL_SCANCODE_RIGHT,

    [MG_KEY_PRINT_SCREEN] = SDL_SCANCODE_PRINTSCREEN,
    [MG_KEY_SCROLL_LOCK] = SDL_SCANCODE_SCROLLLOCK,
    [MG_KEY_PAUSE] = SDL_SCANCODE_PAUSE,

    [MG_KEY_NUM_LOCK] = SDL_SCANCODE_NUMLOCKCLEAR,
    [MG_KEY_NUMPAD_0] = SDL_SCANCODE_KP_0,
    [MG_KEY_NUMPAD_1] = SDL_SCANCODE_KP_1,
    [MG_KEY_NUMPAD_2] = SDL_SCANCODE_KP_2,
    [MG_KEY_NUMPAD_3] = SDL_SCANCODE_KP_3,
    [MG_KEY_NUMPAD_4] = SDL_SCANCODE_KP_4,
    [MG_KEY_NUMPAD_5] = SDL_SCANCODE_KP_5,
    [MG_KEY_NUMPAD_6] = SDL_SCANCODE_KP_6,
    [MG_KEY_NUMPAD_7] = SDL_SCANCODE_KP_7,
    [MG_KEY_NUMPAD_8] = SDL_SCANCODE_KP_8,
    [MG_KEY_NUMPAD_9] = SDL_SCANCODE_KP_9,
    [MG_KEY_NUMPAD_DIVIDE] = SDL_SCANCODE_KP_DIVIDE,
    [MG_KEY_NUMPAD_MULTIPLY] = SDL_SCANCODE_KP_MULTIPLY,
    [MG_KEY_NUMPAD_MINUS] = SDL_SCANCODE_KP_MINUS,
    [MG_KEY_NUMPAD_PLUS] = SDL_SCANCODE_KP_PLUS,
    [MG_KEY_NUMPAD_ENTER] = SDL_SCANCODE_KP_ENTER,
    [MG_KEY_NUMPAD_PERIOD] = SDL_SCANCODE_KP_PERIOD,
};

/**
 * @brief An input array over values with every slot live, so any code
 * indexes without a push. The construct form starts at count 0, which _at
 * treats as empty; input has no notion of a partially-filled code space.
 * @param values The gem's static storage for one code space.
 * @param count How many codes that space has.
 * @return The array, borrowing values.
 */
static mg_input_array sdl_input_array_over(f32* values, const len count) {
    const mg_input_array array = {values, sizeof(f32), count, count, NULL};
    return array;
}

void sdl_note_wheel(const f32 sideways, const f32 steps) {
    g_wheel_sideways += sideways;
    g_wheel_steps += steps;
}

mg_key_code sdl_key_from_scancode(const SDL_Scancode scancode) {
    /* Inverted from the forward map, so the two can never disagree. The
     * unfilled entries stay zero, which is MG_KEY_NONE. */
    if (!g_key_for_scancode_built) {
        for (u32 key = MG_KEY_NONE + 1; key < MG_KEY_COUNT; key++) {
            const SDL_Scancode code = g_scancode_for_key[key];
            if (code != SDL_SCANCODE_UNKNOWN) {
                g_key_for_scancode[code] = (mg_key_code)key;
            }
        }
        g_key_for_scancode_built = TRUE;
    }

    return scancode > SDL_SCANCODE_UNKNOWN && scancode < SDL_SCANCODE_COUNT
        ? g_key_for_scancode[scancode]
        : MG_KEY_NONE;
}

mg_button_code sdl_button_from_mouse(const u8 button) {
    switch (button) {
        case SDL_BUTTON_LEFT:
            return MG_MOUSE_BUTTON_LEFT;
        case SDL_BUTTON_MIDDLE:
            return MG_MOUSE_BUTTON_MIDDLE;
        case SDL_BUTTON_RIGHT:
            return MG_MOUSE_BUTTON_RIGHT;
        case SDL_BUTTON_X1:
            return MG_MOUSE_BUTTON_X1;
        case SDL_BUTTON_X2:
            return MG_MOUSE_BUTTON_X2;
        default:
            return MG_BUTTON_NONE;
    }
}

void sdl_gather_input(mg_input_state* out_input, const u32 screen_width) {
    if (out_input == NULL) {
        return;
    }

    // Keyboard: held is 1, everything else, unknown scancodes included, is 0.
    i32 key_count = 0;
    const bool* held_keys = SDL_GetKeyboardState(&key_count);
    for (u32 key = 0; key < MG_KEY_COUNT; key++) {
        const SDL_Scancode code = g_scancode_for_key[key];
        const b8 held = held_keys != NULL && code != SDL_SCANCODE_UNKNOWN && (i32)code < key_count
            && held_keys[code];
        g_key_values[key] = held ? 1.0f : 0.0f;
    }

    /* Mouse buttons, into controller 0's row -- there is only the one mouse;
     * the position rides along for the axes below. */
    f32 mouse_x = 0;
    f32 mouse_y = 0;
    const SDL_MouseButtonFlags held_buttons = SDL_GetMouseState(&mouse_x, &mouse_y);
    g_button_values[MG_BUTTON_INDEX(0, MG_MOUSE_BUTTON_LEFT)] =
        (held_buttons & SDL_BUTTON_LMASK) ? 1.0f : 0.0f;
    g_button_values[MG_BUTTON_INDEX(0, MG_MOUSE_BUTTON_MIDDLE)] =
        (held_buttons & SDL_BUTTON_MMASK) ? 1.0f : 0.0f;
    g_button_values[MG_BUTTON_INDEX(0, MG_MOUSE_BUTTON_RIGHT)] =
        (held_buttons & SDL_BUTTON_RMASK) ? 1.0f : 0.0f;
    g_button_values[MG_BUTTON_INDEX(0, MG_MOUSE_BUTTON_X1)] =
        (held_buttons & SDL_BUTTON_X1MASK) ? 1.0f : 0.0f;
    g_button_values[MG_BUTTON_INDEX(0, MG_MOUSE_BUTTON_X2)] =
        (held_buttons & SDL_BUTTON_X2MASK) ? 1.0f : 0.0f;

    /* A zero-width window has no coordinates to pack, and the unpack on the
     * engine's side divides by the width. */
    g_axis_values[MG_AXIS_INDEX(0, MG_AXIS_MOUSE)] =
        screen_width > 0 ? mg_pack_mouse_pos(mouse_x, mouse_y, screen_width) : 0.0f;
    /* One value per axis, so the vertical steps -- the usual wheel -- are it;
     * sideways scrolling waits on a packing scheme like the position's. */
    g_axis_values[MG_AXIS_INDEX(0, MG_AXIS_MOUSE_WHEEL)] = g_wheel_steps;
    g_wheel_steps = 0;
    g_wheel_sideways = 0;

    out_input->keyboard = sdl_input_array_over(g_key_values, MG_KEY_COUNT);
    out_input->buttons =
        sdl_input_array_over(g_button_values, (len)MG_MAX_CONTROLLERS * MG_BUTTON_COUNT);
    out_input->analog =
        sdl_input_array_over(g_axis_values, (len)MG_MAX_CONTROLLERS * MG_AXIS_COUNT);
}

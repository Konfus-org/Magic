/**
 * @file input.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The engine's input state and the queries over it. Three snapshots
 * deep -- this frame, last frame, and the one before, which released-last-frame
 * needs. The values live in rows of static storage the update copies INTO:
 * a backend's arrays borrow its own storage, so keeping the structs it hands
 * up would leave every snapshot aliasing the same live values.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/platform/input.h"
#include "magic/primitives.h"

/** @brief How many snapshots the queries look back through. */
#define MG_INPUT_HISTORY 3

/* Rows of values per code space; row 0 is this frame, 1 last, 2 the one
 * before. Buttons and axes are controller-major inside a row -- one code
 * space per controller, MG_MAX_CONTROLLERS deep -- indexed with
 * MG_BUTTON_INDEX and MG_AXIS_INDEX; the mouse reads controller 0. */
static f32 g_key_values[MG_INPUT_HISTORY][MG_KEY_COUNT];
static f32 g_button_values[MG_INPUT_HISTORY][MG_MAX_CONTROLLERS * MG_BUTTON_COUNT];
static f32 g_axis_values[MG_INPUT_HISTORY][MG_MAX_CONTROLLERS * MG_AXIS_COUNT];

/** @brief The screen the packed mouse position unpacks against. */
static s32 g_screen_size;

/** @brief The value at code in array, or 0 for an array too small to hold it. */
static f32 mg_input_value(mg_input_array* array, const ind code) {
    const f32* value = mg_input_array_at(array, code);
    return value != NULL ? *value : 0.0f;
}

/* Shifts rows toward history and copies the incoming array into row 0. A
 * macro rather than a function: a two-dimensional parameter needs its inner
 * extent spelled, and it differs per code space. */
#define MG_SHIFT_INPUT_VALUES_(values, incoming, count)                                            \
    do {                                                                                           \
        for (len row = MG_INPUT_HISTORY - 1; row > 0; row--) {                                     \
            for (len code = 0; code < (count); code++) {                                           \
                (values)[row][code] = (values)[row - 1][code];                                     \
            }                                                                                      \
        }                                                                                          \
        for (len code = 0; code < (count); code++) {                                               \
            (values)[0][code] = mg_input_value((incoming), code);                                  \
        }                                                                                          \
    } while (0)

void mg_update_input(mg_input_state state, const s32 screen_size) {
    MG_SHIFT_INPUT_VALUES_(g_key_values, &state.keyboard, MG_KEY_COUNT);
    MG_SHIFT_INPUT_VALUES_(
        g_button_values,
        &state.buttons,
        (len)MG_MAX_CONTROLLERS * MG_BUTTON_COUNT);
    MG_SHIFT_INPUT_VALUES_(g_axis_values, &state.analog, (len)MG_MAX_CONTROLLERS * MG_AXIS_COUNT);
    g_screen_size = screen_size;
}

b8 mg_was_key_pressed(const mg_key_code key) {
    return g_key_values[1][key] > 0.0f;
}

b8 mg_was_key_released(const mg_key_code key) {
    return g_key_values[1][key] <= 0.0f && g_key_values[2][key] > 0.0f;
}

b8 mg_is_key_pressed(const mg_key_code key) {
    return g_key_values[0][key] > 0.0f;
}

b8 mg_is_key_released(const mg_key_code key) {
    return g_key_values[0][key] <= 0.0f && g_key_values[1][key] > 0.0f;
}

b8 mg_was_button_pressed(const mg_button_code button, const ind controller) {
    return controller < MG_MAX_CONTROLLERS
        && g_button_values[1][MG_BUTTON_INDEX(controller, button)] > 0.0f;
}

b8 mg_was_button_released(const mg_button_code button, const ind controller) {
    if (controller >= MG_MAX_CONTROLLERS) {
        return FALSE;
    }
    const ind slot = MG_BUTTON_INDEX(controller, button);
    return g_button_values[1][slot] <= 0.0f && g_button_values[2][slot] > 0.0f;
}

b8 mg_is_button_pressed(const mg_button_code button, const ind controller) {
    return controller < MG_MAX_CONTROLLERS
        && g_button_values[0][MG_BUTTON_INDEX(controller, button)] > 0.0f;
}

b8 mg_is_button_released(const mg_button_code button, const ind controller) {
    if (controller >= MG_MAX_CONTROLLERS) {
        return FALSE;
    }
    const ind slot = MG_BUTTON_INDEX(controller, button);
    return g_button_values[0][slot] <= 0.0f && g_button_values[1][slot] > 0.0f;
}

b8 mg_was_mouse_button_pressed(const mg_button_code button) {
    return mg_was_button_pressed(button, 0);
}

b8 mg_was_mouse_button_released(const mg_button_code button) {
    return mg_was_button_released(button, 0);
}

b8 mg_is_mouse_button_pressed(const mg_button_code button) {
    return mg_is_button_pressed(button, 0);
}

b8 mg_is_mouse_button_released(const mg_button_code button) {
    return mg_is_button_released(button, 0);
}

f32 mg_get_tilt(const mg_axis_code axis, const ind controller) {
    return controller < MG_MAX_CONTROLLERS
        ? g_axis_values[0][MG_AXIS_INDEX(controller, axis)]
        : 0.0f;
}

void mg_get_mouse_position(f32* x, f32* y) {
    mg_unpack_mouse_pos(
        g_axis_values[0][MG_AXIS_INDEX(0, MG_AXIS_MOUSE)],
        g_screen_size.width,
        x,
        y);
}

void mg_get_mouse_delta(f32* x, f32* y) {
    f32 was_pos_x = 0;
    f32 was_pos_y = 0;
    f32 is_pos_x = 0;
    f32 is_pos_y = 0;
    mg_unpack_mouse_pos(
        g_axis_values[1][MG_AXIS_INDEX(0, MG_AXIS_MOUSE)],
        g_screen_size.width,
        &was_pos_x,
        &was_pos_y);
    mg_unpack_mouse_pos(
        g_axis_values[0][MG_AXIS_INDEX(0, MG_AXIS_MOUSE)],
        g_screen_size.width,
        &is_pos_x,
        &is_pos_y);
    *x = is_pos_x - was_pos_x;
    *y = is_pos_y - was_pos_y;
}

f32 mg_get_mouse_wheel(void) {
    return g_axis_values[0][MG_AXIS_INDEX(0, MG_AXIS_MOUSE_WHEEL)];
}

f32 mg_pack_mouse_pos(const f32 x, const f32 y, const u32 screen_width) {
    return (y * (f32)screen_width) + x;
}

void mg_unpack_mouse_pos(const f32 packed, const u32 screen_width, f32* out_x, f32* out_y) {
    /* A zero-width screen has no columns to divide the index back into. */
    if (screen_width == 0) {
        *out_x = 0;
        *out_y = 0;
        return;
    }

    const u32 linear_index = (u32)packed;
    /* Integer division, matching the pack: a float divide would fold x/width
     * into y. */
    const u32 column = linear_index % screen_width;
    const u32 row = linear_index / screen_width;
    *out_x = (f32)column;
    *out_y = (f32)row;
}

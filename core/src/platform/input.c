/**
 * @file input.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief GENERATED-STUBS -- every function here traps when called.
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/platform/input.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"

void mg_update_input(const mg_input_state state) {
    MG_UNUSED(state);
    MG_NOT_IMPLEMENTED();
}

b8 mg_was_key_pressed(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_was_key_released(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_was_key_down(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_key_pressed(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_key_released(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_key_down(const mg_key_code key) {
    MG_UNUSED(key);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_was_button_pressed(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_was_button_released(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_was_button_down(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_button_pressed(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_button_released(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

b8 mg_is_button_down(const mg_button_code button) {
    MG_UNUSED(button);
    MG_NOT_IMPLEMENTED_RETURN(b8);
}

f32 mg_get_tilt(const mg_axis_code axis) {
    MG_UNUSED(axis);
    MG_NOT_IMPLEMENTED_RETURN(f32);
}

void mg_get_mouse_position(f32* x, f32* y) {
    MG_UNUSED(x, y);
    MG_NOT_IMPLEMENTED();
}

void mg_get_mouse_delta(f32* x, f32* y) {
    MG_UNUSED(x, y);
    MG_NOT_IMPLEMENTED();
}

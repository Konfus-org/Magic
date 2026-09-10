/**
 * @file input.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-10
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/export.h"
#include "magic/platform/input_codes.h"
#include "magic/platform/state.h"
#include "magic/primitives.h"

/**@brief Updates the global input state. */
void mg_update_input(mg_input_state state);

/** @brief Returns true if the key was pressed last frame */
MG_EXPORT b8 mg_was_key_pressed(mg_key_code key);
/** @brief Returns true if the key was released last frame */
MG_EXPORT b8 mg_was_key_released(mg_key_code key);
/** @brief Returns true if the key was held down last frame */
MG_EXPORT b8 mg_was_key_down(mg_key_code key);

/** @brief Returns true if the key was pressed this frame */
MG_EXPORT b8 mg_is_key_pressed(mg_key_code key);
/** @brief Returns true if the key was released this frame */
MG_EXPORT b8 mg_is_key_released(mg_key_code key);
/** @brief Returns true if the key is currently held down */
MG_EXPORT b8 mg_is_key_down(mg_key_code key);

/** @brief Returns true if the button was pressed last frame */
MG_EXPORT b8 mg_was_button_pressed(mg_button_code button);
/** @brief Returns true if the button was released last frame */
MG_EXPORT b8 mg_was_button_released(mg_button_code button);
/** @brief Returns true if the button was held down last frame */
MG_EXPORT b8 mg_was_button_down(mg_button_code button);

/** @brief Returns true if the button was pressed this frame */
MG_EXPORT b8 mg_is_button_pressed(mg_button_code button);
/** @brief Returns true if the button was released this frame */
MG_EXPORT b8 mg_is_button_released(mg_button_code button);
/** @brief Returns true if the button is currently held down */
MG_EXPORT b8 mg_is_button_down(mg_button_code button);

/** @brief Gets the value for the given axis */
MG_EXPORT f32 mg_get_tilt(mg_axis_code axis);

/** @brief Gets the mouse's current position, in window coordinates */
MG_EXPORT void mg_get_mouse_position(f32* x, f32* y);
/** @brief Gets how far the mouse moved since last frame */
MG_EXPORT void mg_get_mouse_delta(f32* x, f32* y);

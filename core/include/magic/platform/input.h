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
void mg_update_input(mg_input_state state, s32 screen_size);

/** @brief Returns true if the key was pressed last frame */
MG_EXPORT b8 mg_was_key_pressed(mg_key_code key);
/** @brief Returns true if the key was released last frame */
MG_EXPORT b8 mg_was_key_released(mg_key_code key);

/** @brief Returns true if the key was pressed this frame */
MG_EXPORT b8 mg_is_key_pressed(mg_key_code key);
/** @brief Returns true if the key was released this frame */
MG_EXPORT b8 mg_is_key_released(mg_key_code key);

/** @brief Returns true if the button was pressed last frame on the given controller */
MG_EXPORT b8 mg_was_button_pressed(mg_button_code button, ind controller);
/** @brief Returns true if the button was released last frame on the given controller */
MG_EXPORT b8 mg_was_button_released(mg_button_code button, ind controller);

/** @brief Returns true if the button is pressed this frame on the given controller */
MG_EXPORT b8 mg_is_button_pressed(mg_button_code button, ind controller);
/** @brief Returns true if the button was released this frame on the given controller */
MG_EXPORT b8 mg_is_button_released(mg_button_code button, ind controller);

/* The mouse endpoints: there is only ever the one mouse, so these pass slot
 * 0 where a controller would go, and no call site spells it. */

/** @brief Returns true if the mouse button was pressed last frame */
MG_EXPORT b8 mg_was_mouse_button_pressed(mg_button_code button);
/** @brief Returns true if the mouse button was released last frame */
MG_EXPORT b8 mg_was_mouse_button_released(mg_button_code button);

/** @brief Returns true if the mouse button is pressed this frame */
MG_EXPORT b8 mg_is_mouse_button_pressed(mg_button_code button);
/** @brief Returns true if the mouse button was released this frame */
MG_EXPORT b8 mg_is_mouse_button_released(mg_button_code button);

/** @brief Gets the value for the given axis on the given controller */
MG_EXPORT f32 mg_get_tilt(mg_axis_code axis, ind controller);

/** @brief Gets the mouse's current position, in window coordinates */
MG_EXPORT void mg_get_mouse_position(f32* x, f32* y);
/** @brief Gets how far the mouse moved since last frame */
MG_EXPORT void mg_get_mouse_delta(f32* x, f32* y);
/** @brief Gets how many steps the mouse wheel turned this frame; y is the usual wheel. */
MG_EXPORT f32 mg_get_mouse_wheel(void);

// Encoding: 2D -> 1D Float
MG_EXPORT f32 mg_pack_mouse_pos(f32 x, f32 y, u32 screen_width);

// Decoding: 1D Float -> 2D
MG_EXPORT void mg_unpack_mouse_pos(f32 packed, u32 screen_width, f32* out_x, f32* out_y);

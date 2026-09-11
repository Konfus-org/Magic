/**
 * @file sdl_time.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Time: sleeping, the ticking clock and the calendar one, via SDL.
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "sdl_platform.h"
#include <SDL3/SDL.h>

void sdl_sleep(const ts ms) {
    if (ms > 0) {
        SDL_DelayNS((u64)(ms * 1.0e6));
    }
}

/**
 * @brief gs_platform_get_timestamp via SDL.
 * @return The time the platform has been ticking, in milliseconds.
 */
ts sdl_get_timestamp(void) {
    const u64 ns = SDL_GetTicksNS();
    return (ts)ns / 1.0e6;
}

/**
 * @brief gs_platform_get_datetime via SDL.
 * @return The current local date and time, zeroed if the clock is unreadable.
 */
mg_datetime sdl_get_datetime(void) {
    mg_datetime now = {0, 0, 0, 0, 0};

    SDL_Time time = 0;
    SDL_DateTime date = {0};
    if (SDL_GetCurrentTime(&time) && SDL_TimeToDateTime(time, &date, TRUE)) {
        now.milliseconds = (ts)date.nanosecond / 1.0e6;
        now.seconds = (ts)date.second;
        now.day = (ts)date.day;
        now.month = (ts)date.month;
        now.year = (ts)date.year;
    }

    return now;
}

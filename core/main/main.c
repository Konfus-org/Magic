/**
 * @file main.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The callback stub: the main() an app inherits by linking Magic::main.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/app.h"

/* Deliberately a static library and never part of the engine: a name resolves
 * inside the binary that spells it, so the six mg_on_* names below have to be
 * spelled in the app's own binary, where the app's definitions live -- spelled
 * from a shared magic they would be six unresolved externals. Compiled into
 * the app, they resolve at its link like any other call, and what crosses into
 * the engine is an mg_app_callbacks of plain pointers. The same trick as SDL's
 * SDL_main.h, as a target instead of a header an app has to include. */
int main(const int argc, const char** argv) {
    const mg_app_callbacks callbacks = {
        .on_init = mg_on_init,
        .on_start = mg_on_start,
        .on_event = mg_on_event,
        .on_update = mg_on_update,
        .on_fixed_update = mg_on_fixed_update,
        .on_exit = mg_on_exit,
    };
    return mg_app_run(argc, argv, &callbacks);
}

# SDL_platform: the platform backend -- window, events, input, time, files.
# Requires SDL_base at run time, which is declared in sdl_platform_gem.c and
# nowhere here: gems are loaded, not linked, so a dependency between two of
# them is not a link-time relationship and CMake has no part in it.
mg_add_gem(SDL_platform LINK SDL3::SDL3-shared)

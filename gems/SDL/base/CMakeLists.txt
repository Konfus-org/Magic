# SDL_base: SDL brought up, allocating through mg_alloc and logging through
# the MG_* macros. Fills no slot -- what it provides is that state, which
# SDL_platform and SDL_gpu both name with MG_GEM_REQUIRES.
#
# The MG_GEM_SDL_BASE option mg_add_gem gates this on is declared one
# directory up, so turning it off also skips the SDL download.
mg_add_gem(SDL_base LINK SDL3::SDL3-shared)

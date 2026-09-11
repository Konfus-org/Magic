# SDL_gpu: the render backend, drawing through SDL_GPU onto the window the
# platform backend opened. Requires SDL_base at run time, declared in
# sdl_gpu_gem.c; see the note in ../platform/CMakeLists.txt on why that is not
# a CMake dependency.
mg_add_gem(SDL_gpu LINK SDL3::SDL3-shared)

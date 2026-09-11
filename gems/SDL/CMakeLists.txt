# The SDL gems. One SDL, split three ways:
#
#   SDL_base      brings SDL up and wires it into the engine's memory and log
#   SDL_platform  window, events, input, time and files    -> requires SDL_base
#   SDL_gpu       the SDL_GPU render backend               -> requires SDL_base
#
# The requirement is declared in each gem's MG_PROVIDE_GEM, and the loader
# orders the folder by it, so the base's on_load runs before either of the
# others' -- nothing here has to arrange that, and nothing here can.
#
# SDL itself arrives via FetchContent at configure time, so a fresh checkout
# needs nothing installed to build these.

# mg_add_gem declares each of these below; declaring them first lets a build
# with every SDL gem off skip the SDL download entirely, not just the compiles.
option(MG_GEM_SDL_BASE "Build the SDL_base gem" ON)
option(MG_GEM_SDL_PLATFORM "Build the SDL_platform gem" ON)
option(MG_GEM_SDL_GPU "Build the SDL_gpu gem" ON)

if (NOT MG_GEM_SDL_BASE AND NOT MG_GEM_SDL_PLATFORM AND NOT MG_GEM_SDL_GPU)
    message(STATUS "GEMS 'SDL': all disabled")
    return()
endif ()

# Built, but never loadable: the loader refuses a gem whose dependency the
# folder has none of, so this would otherwise only show up at run time.
if (NOT MG_GEM_SDL_BASE AND (MG_GEM_SDL_PLATFORM OR MG_GEM_SDL_GPU))
    message(WARNING "MG_GEM_SDL_BASE is OFF, so the SDL gems built here will "
            "not load: both require SDL_base.")
endif ()

include(FetchContent)

# Shared, and deliberately: the three gems are three modules in one process,
# and SDL's state -- the subsystem refcounts, the allocator, the log writer,
# the event queue -- has to be one copy for them to be talking about the same
# SDL. Linked statically, each gem would carry a private SDL, SDL_base would
# initialize one nobody else can see, and the split would mean nothing.
set(SDL_SHARED ON)
set(SDL_STATIC OFF)
set(SDL_TEST_LIBRARY OFF)

# SDL3.dll lands where the apps do, not where the gems do: a gem is loaded
# from the gems folder, but Windows resolves that module's own imports against
# the executable's directory and never against the module's. Set before
# MakeAvailable so SDL's targets are created with it.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${MG_APPS_OUTPUT_DIR})
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${MG_APPS_OUTPUT_DIR})

# SYSTEM keeps MG_C_WARNINGS from firing inside SDL's headers, and
# EXCLUDE_FROM_ALL keeps SDL targets nobody links out of the default build.
FetchContent_Declare(SDL3
        GIT_REPOSITORY https://github.com/libsdl-org/SDL.git
        GIT_TAG release-3.4.16
        GIT_SHALLOW TRUE
        SYSTEM
        EXCLUDE_FROM_ALL
)

# SDL is not held to Magic's clang-tidy checks: cleared for the subdirectory,
# restored so the gems' own targets still get it.
set(saved_clang_tidy "${CMAKE_C_CLANG_TIDY}")
set(CMAKE_C_CLANG_TIDY "")
FetchContent_MakeAvailable(SDL3)
set(CMAKE_C_CLANG_TIDY "${saved_clang_tidy}")

# SDL's stdlib calls the CRT's POSIX-named and non-_s functions (itoa, _ltoa)
# on purpose; without these gates the Windows headers mark them deprecated and
# SDL's own compile warns. SYSTEM above only quiets SDL's headers in our code.
if (WIN32)
    target_compile_definitions(SDL3-shared PRIVATE
            _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_DEPRECATE)
endif ()

# One directory per gem, each with its own mg_add_gem. Base first, which is
# only the reading order: what actually orders them is each gem's own
# MG_GEM_REQUIRES, read by the loader at run time.
add_subdirectory(base)
add_subdirectory(platform)
add_subdirectory(gpu)

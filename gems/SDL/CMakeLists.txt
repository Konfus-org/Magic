# The SDL platform gem. SDL itself arrives via FetchContent at configure time,
# so a fresh checkout needs nothing installed to build this gem.

# mg_add_gem declares this same option below; declaring it first lets a
# disabled gem skip the SDL download entirely, not just its own compile.
option(MG_GEM_SDL "Build the SDL gem" ON)
if (NOT MG_GEM_SDL)
    message(STATUS "GEM 'SDL': disabled (MG_GEM_SDL=OFF)")
    return()
endif ()

include(FetchContent)

# Static, so the gem stays one self-contained file: gems load from their own
# folder, which is not on the search path an SDL3.dll would have to be on.
set(SDL_SHARED OFF)
set(SDL_STATIC ON)
set(SDL_TEST_LIBRARY OFF)

# A static library linked into a MODULE has to be PIC on ELF platforms;
# harmless everywhere else.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

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
# restored so the gem's own target still gets it.
set(saved_clang_tidy "${CMAKE_C_CLANG_TIDY}")
set(CMAKE_C_CLANG_TIDY "")
FetchContent_MakeAvailable(SDL3)
set(CMAKE_C_CLANG_TIDY "${saved_clang_tidy}")

# SDL's stdlib calls the CRT's POSIX-named and non-_s functions (itoa, _ltoa)
# on purpose; without these gates the Windows headers mark them deprecated and
# SDL's own compile warns. SYSTEM above only quiets SDL's headers in our code.
if (WIN32)
    target_compile_definitions(SDL3-static PRIVATE
            _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_DEPRECATE)
endif ()

mg_add_gem(SDL LINK SDL3::SDL3-static)

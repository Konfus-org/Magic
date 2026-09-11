# Resolves what the target platform is called: MG_PLATFORM_DEFINE, the one
# platform macro every Magic translation unit is compiled with, and
# MG_PLATFORM_DIR, the folder name build output lands under. Included from
# the top level so sibling directories (core/, tests/) inherit both.

include_guard(GLOBAL)

# Resolved from CMAKE_SYSTEM_NAME rather than WIN32/APPLE so a cross-compile
# reports the target platform and not the host. There is deliberately no
# fallback: an unhandled platform fails here, loudly, instead of silently
# missing every #ifdef in the C sources.
#
# The macro names are the ones core/include/magic/platform/defines.h derives
# from the compiler's predefined macros when nothing on the command line set
# one -- both halves have to agree on the spelling.
if(CMAKE_SYSTEM_NAME STREQUAL "Windows")
    set(MG_PLATFORM_DEFINE MG_PLATFORM_WINDOWS)
    set(MG_PLATFORM_DIR windows)
elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(MG_PLATFORM_DEFINE MG_PLATFORM_IOS)
    set(MG_PLATFORM_DIR ios)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(MG_PLATFORM_DEFINE MG_PLATFORM_LINUX)
    set(MG_PLATFORM_DIR linux)
else()
    message(FATAL_ERROR
            "Magic has no platform macro for CMAKE_SYSTEM_NAME "
            "'${CMAKE_SYSTEM_NAME}'. Add one next to MG_PLATFORM_WINDOWS, "
            "MG_PLATFORM_IOS and MG_PLATFORM_LINUX.")
endif()

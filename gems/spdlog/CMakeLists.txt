# The spdlog logging gem. spdlog arrives via FetchContent at configure time,
# so a fresh checkout needs nothing installed to build this gem.

# mg_add_gem declares this same option below; declaring it first lets a
# disabled gem skip the spdlog download entirely, not just its own compile.
option(MG_GEM_SPDLOG "Build the spdlog gem" ON)
if (NOT MG_GEM_SPDLOG)
    message(STATUS "GEM 'spdlog': disabled (MG_GEM_SPDLOG=OFF)")
    return()
endif ()

# spdlog is C++ and the top-level project() declares LANGUAGES C, so the C++
# compiler is found here -- and only when this gem is actually being built, the
# way tests/ enables it only when MG_BUILD_TESTS is on.
enable_language(CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

set(SPDLOG_BUILD_EXAMPLE OFF)
set(SPDLOG_BUILD_TESTS OFF)
set(SPDLOG_BUILD_BENCH OFF)
set(SPDLOG_INSTALL OFF)

# A static library linked into a MODULE has to be PIC on ELF platforms;
# harmless everywhere else.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# SYSTEM keeps this project's warnings from firing inside spdlog's headers, and
# EXCLUDE_FROM_ALL keeps targets nobody links out of the default build.
FetchContent_Declare(spdlog
        GIT_REPOSITORY https://github.com/gabime/spdlog.git
        GIT_TAG v1.15.3
        GIT_SHALLOW TRUE
        SYSTEM
        EXCLUDE_FROM_ALL
)

# spdlog reads BUILD_SHARED_LIBS as well as its own SPDLOG_BUILD_SHARED, and
# the engine defaults to shared -- which would leave the gem needing an
# spdlog.dll that its own folder has no copy of. A normal variable shadows the
# cache entry for this directory and its children, exactly as tests/ pins
# GoogleTest static; the unset puts the cache back in view.
set(BUILD_SHARED_LIBS OFF)
FetchContent_MakeAvailable(spdlog)
unset(BUILD_SHARED_LIBS)

# ==============================================================================
# The sink
# ==============================================================================
# The C++ half, in a library of its own. mg_add_gem builds a gem from the .c
# files beside it and compiles them with MG_C_WARNINGS, which a C++ front end
# rejects outright -- -Wstrict-prototypes, -Wmissing-prototypes and
# -Wold-style-definition have no meaning there -- so the one .cpp in this gem
# is compiled on its own terms and linked in. tests/CMakeLists.txt makes the
# same call for the same reason.
add_library(mg_spdlog_sink STATIC spdlog_sink.cpp)

target_link_libraries(mg_spdlog_sink PRIVATE spdlog::spdlog)

# Its own directory, so spdlog_gem.h is included by plain name.
target_include_directories(mg_spdlog_sink PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

# Hidden like the gem it is linked into: spdlog is this gem's business, and
# nothing of it belongs in the ABI the loader binds against.
set_target_properties(mg_spdlog_sink PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON
)

# The intent behind MG_C_WARNINGS at the level a single C++ file needs, and
# without -Werror: this file is glue, and a new compiler growing a new opinion
# about spdlog's headers should not stop the gem building.
if (CMAKE_CXX_COMPILER_ID MATCHES "^(Apple)?Clang$|^GNU$")
    target_compile_options(mg_spdlog_sink PRIVATE -Wall -Wextra -Wpedantic -Wshadow)
elseif (CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
    target_compile_options(mg_spdlog_sink PRIVATE /W4 /permissive-)
endif ()

# ==============================================================================
# The gem
# ==============================================================================
mg_add_gem(spdlog LINK mg_spdlog_sink)

# The gem's own sources are C, so CMake would pick the C driver to link with
# and leave the C++ runtime the sink needs out of it. mg_add_gem names its
# target mg_gem_<name>.
set_target_properties(mg_gem_spdlog PROPERTIES LINKER_LANGUAGE CXX)

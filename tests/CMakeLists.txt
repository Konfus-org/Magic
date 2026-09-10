# The unit tests. One executable over every .cpp under unit/, run by CTest.
#
# C++ and not C, for two reasons that happen to agree. GoogleTest is a C++
# framework, so a C test suite was never on the table -- and the umbrella header
# at bindings/cpp/magic.h is the C++ binding, so a test suite written in C++
# against it is also the thing that proves the binding works. A test file
# includes <magic.h> and nothing else; if that header ever stops compiling as
# C++, or stops declaring something with C linkage, this suite fails to build
# rather than a consumer finding out later.

# The QA app, first and in plain C: everything below this line is the C++
# GoogleTest world, which qa/ does not need.
add_subdirectory(qa)

# Enabled here rather than in the top-level project(), so a build with
# MG_BUILD_TESTS off never looks for a C++ compiler at all. Magic itself is
# a C library and has no C++ in it.
enable_language(CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ==============================================================================
# GoogleTest
# ==============================================================================
include(FetchContent)

FetchContent_Declare(googletest
        GIT_REPOSITORY https://github.com/google/googletest.git
        GIT_TAG v1.15.2
        GIT_SHALLOW ON
)

# Not a preference: it has to match how the engine was built or the two link
# against different C runtimes. The clang presets compile core/ against the
# dynamic CRT, and googletest defaults to the static one on Windows -- which
# links, and then hands the test process two heaps, so memory allocated by
# mg_alloc and freed inside a test is freed by the wrong one.
# Ignored everywhere but MSVC-ABI Windows.
set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)

# Nothing here is ours to install.
set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)

# GoogleTest has no reason to follow the engine's link mode: shared gtest
# DLLs are just more files beside the test binary. A normal variable shadows
# the BUILD_SHARED_LIBS cache entry for this directory and its children, so
# the framework is pinned static and the unset puts the cache back in view.
set(BUILD_SHARED_LIBS OFF)
FetchContent_MakeAvailable(googletest)
unset(BUILD_SHARED_LIBS)

# ==============================================================================
# The suite
# ==============================================================================
file(GLOB MG_UNIT_TEST_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/unit/*.cpp")

if (NOT MG_UNIT_TEST_SOURCES)
    message(FATAL_ERROR "MG_BUILD_TESTS is on but tests/unit/ holds no .cpp files.")
endif ()

add_executable(mg_unit_tests ${MG_UNIT_TEST_SOURCES})

# Magic::cpp and not Magic::magic: it is the umbrella target, and it brings the
# engine with it. Linking the library directly would compile these tests
# without ever touching bindings/cpp/magic.h.
target_link_libraries(mg_unit_tests PRIVATE Magic::cpp GTest::gtest_main)

# MG_C_WARNINGS is deliberately not reused. It is a C warning set, and
# several of its flags -- -Wstrict-prototypes, -Wmissing-prototypes,
# -Wold-style-definition, -Wnested-externs, -Wbad-function-cast -- are rejected
# outright by a C++ front end. What follows is the same intent at the level a
# test file needs, and without -Werror: a test that stops building because a new
# compiler grew a new opinion is a test that stops running.
# -Wno-unused-function, because the umbrella expands MG_DEFINE_LIST for
# every primitive into every test file. The minted functions are static inline,
# which C exempts from -Wunused-function precisely so headers can do this --
# C++ does not, and a test that calls three of the six operations would drown
# in warnings about the other three.
if (CMAKE_CXX_COMPILER_ID MATCHES "^(Apple)?Clang$|^GNU$")
    target_compile_options(mg_unit_tests PRIVATE
            -Wall -Wextra -Wpedantic -Wshadow -Wno-unused-function)
elseif (CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
    target_compile_options(mg_unit_tests PRIVATE /W4 /permissive-)
endif ()

# ==============================================================================
# CTest
# ==============================================================================
include(GoogleTest)

# PRE_TEST defers enumerating the test cases until `ctest` runs, rather than
# executing the freshly linked binary during the build. With BUILD_SHARED_LIBS
# on, that binary needs magic.dll beside it, which it only has once the build
# has finished copying things into place -- so build-time discovery is a
# failure that depends on the link mode, and this one is not.
gtest_discover_tests(mg_unit_tests
        DISCOVERY_MODE PRE_TEST
        PROPERTIES LABELS unit
)

source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} PREFIX tests FILES ${MG_UNIT_TEST_SOURCES})

message(STATUS "Magic: unit tests enabled; `ctest --test-dir <build>` runs them.")

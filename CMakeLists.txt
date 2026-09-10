cmake_minimum_required(VERSION 3.30)
project(MagicGameEngine LANGUAGES C VERSION 1.0.0)

# The modules under cmake/ are found by plain name from here down, so core/ and
# any sibling directory added later say include(MagicCompiler) and not a path.
list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")

# ==============================================================================
# Global C Standard Enforcements
# ==============================================================================
# C11 and not something newer, deliberately: the argument-count dispatch in
# asserts.h and list.h is written the way it is because C11 has neither
# __VA_OPT__ nor, with extensions off, the GNU ##__VA_ARGS__ comma swallow.
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

# ==============================================================================
# Build Options
# ==============================================================================

# Defaults to SHARED: one magic.dll in the process, shared by the app and every
# gem, so there is exactly one copy of the engine's globals -- statically
# linked, each gem carries its own private copies and an event queued on the
# gem side never reaches the app's queue. Can be changed via
# -DBUILD_SHARED_LIBS=OFF.
option(BUILD_SHARED_LIBS "Build libraries as shared objects (.dll/.so/.dylib) instead of static (.lib/.a)" ON)

# Read by MagicCompiler, so it has to be declared before that module runs.
option(MG_WERROR "Treat compiler warnings as errors" ON)

# The gates the headers read, under the exact names the headers spell them, so
# option, preset key and C macro are one vocabulary. Each is always passed on
# the command line rather than left to the header's #ifndef fallback: -Wundef is
# an error here, so a misspelled gate cannot be quietly read as 0.
option(MG_ASSERT_ENABLED "Compile runtime assertions in" ON)
option(MG_ERROR_ENABLED "Compile MG_ERROR in" ON)
option(MG_WARNING_ENABLED "Compile MG_WARNING in" ON)
option(MG_INFO_ENABLED "Compile MG_INFO in" ON)
option(MG_DEBUG_ENABLED "Compile MG_DEBUG in" OFF)
option(MG_TRACE_ENABLED "Compile MG_TRACE in" OFF)

# The per-tag byte ledger behind magic/memory/stats.h. Its cost is not the
# counting but the bookkeeping header in front of every allocation, which is
# the only way a size-free mg_free can report what it gave back. Off, the
# header is not compiled at all and mg_alloc hands the allocator's own pointer
# straight through; the ledger then reads zero, so the memory tests skip
# themselves rather than fail.
option(MG_TRACK_MEMORY "Count allocated and deallocated bytes per tag" ON)

# Reloads gems from throwaway copies, so a rebuild can overwrite the original
# while the engine runs. Development machinery, so it defaults off.
option(MG_HOT_RELOAD_GEMS "Compile gem hot reloading in" OFF)

# Callback hosting: the Magic::main stub owns main() and the engine drives an
# app through the five mg_on_* functions it defines -- see magic/app.h. The
# stub is a static library compiled into each app, whatever
# BUILD_SHARED_LIBS says, because that is where the five names can resolve;
# the engine itself receives them as pointers through mg_app_run and never
# reads this gate. Passed through with the log gates in core/ for the header's
# extern block.
option(MG_USE_CALLBACKS "Build the Magic::main stub and declare the mg_on_* app callbacks" ON)

# Injects core's src/pch.h ahead of every engine translation unit. OFF is the
# honesty check: a .c leaning on the PCH for a header it never includes stops
# compiling.
option(MG_PCH "Precompile core's src/pch.h for the engine's own sources" ON)

# The unit tests are C++: GoogleTest is a C++ framework, and the umbrella header
# they include is the C++ binding they exist to keep honest. Turning this off
# leaves a pure C project with no network step.
option(MG_BUILD_TESTS "Build the GoogleTest unit tests" ON)

include(MagicPlatform)
include(MagicCompiler)

# ==============================================================================
# Output Layout
# ==============================================================================
# bin/<platform> for what runs, lib/<platform> for what links. No <config>
# segment: each preset owns a whole build directory, so the config is already
# in the path. Deliberately genex-free, which also keeps a multi-config
# generator safe -- CMake appends its own per-config subdirectory to a plain
# output dir. RUNTIME is exes and DLLs in one folder, because Windows
# resolves magic.dll from the folder the .exe sits in; on ELF and Mach-O the
# shared library lands in lib/ and executables reach it through the build
# rpath CMake writes by default. MG_PLATFORM_DIR came from MagicPlatform with
# the platform macro. Set before MagicTargets is included: mg_add_app reads
# CMAKE_RUNTIME_OUTPUT_DIRECTORY to land executables beside the engine
# library, and gems default to a folder inside it.
set(BIN_OUTPUT_DIR "${CMAKE_BINARY_DIR}/bin/${MG_PLATFORM_DIR}")
set(LIB_OUTPUT_DIR "${CMAKE_BINARY_DIR}/lib/${MG_PLATFORM_DIR}")

set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${LIB_OUTPUT_DIR}") # .lib, .a
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${LIB_OUTPUT_DIR}") # .so, .dylib
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${BIN_OUTPUT_DIR}") # .exe, .dll

include(MagicTargets)

# ==============================================================================
# Static Analysis
# ==============================================================================
# Runs clang-tidy on every C compile from here down. The checks live in
# .clang-tidy at the repo root, and it says WarningsAsErrors: '*', so a finding
# fails the build the same way MG_WERROR makes a compiler warning fail it.
option(MG_CLANG_TIDY "Run clang-tidy on every C compile" ON)
if (MG_CLANG_TIDY)
    find_program(MG_CLANG_TIDY_EXE clang-tidy)
    if (MG_CLANG_TIDY_EXE)
        set(CMAKE_C_CLANG_TIDY "${MG_CLANG_TIDY_EXE}")
    else ()
        message(WARNING "MG_CLANG_TIDY is ON but no clang-tidy was found; "
                "building without static analysis.")
    endif ()
endif ()

# ==============================================================================
# Targets
# ==============================================================================
add_subdirectory(core)
add_subdirectory(gems)

# enable_testing() has to be called from the top level, not from tests/: CTest
# writes its test list into the binary directory of the directory that enabled
# it, and `ctest` run from the build root looks for it there.
if (MG_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif ()

# apps/ is not in this tree yet. MagicTargets.cmake is included above so
# mg_add_app is ready when it is.

# Declares the two loadable-target kinds: mg_add_gem() for the MODULE
# libraries the engine loads at run time, and mg_add_app() for the
# executables that host it. A gem is loaded by a host and an app is that host,
# so the two are the same target with the ends swapped -- everything they share
# lives once in _mg_add_module, and each wrapper says only what differs.

include_guard(GLOBAL)

# Where built gems land: their own folder, because mg_load_gems enumerates a
# directory, and hot reload writes <gem>.<n>.tmp copies beside the original.
# Under the runtime output directory when there is one, so an app run from its
# own folder finds "Gems", the engine's relative default, sitting right there.
# Guarded so a parent project can point it elsewhere before the include;
# INTERNAL because sibling directories have to read it too. MG_TARGET_DIR is
# the vendoring hook: a parent project's own output root, never set here.
if(NOT DEFINED MG_GEMS_OUTPUT_DIR)
    if(DEFINED MG_TARGET_DIR)
        set(gems_output_dir ${MG_TARGET_DIR}/gems)
    elseif(CMAKE_RUNTIME_OUTPUT_DIRECTORY)
        set(gems_output_dir ${CMAKE_RUNTIME_OUTPUT_DIRECTORY}/gems)
    else()
        set(gems_output_dir ${CMAKE_BINARY_DIR}/gems)
    endif()
    set(MG_GEMS_OUTPUT_DIR ${gems_output_dir} CACHE INTERNAL "Where built gems land")
endif()

# Where built apps land: beside the engine library, which is what makes an
# executable runnable straight out of the build tree -- Windows resolves
# magic.dll from the folder the .exe sits in, with no rpath to lean on.
if(NOT DEFINED MG_APPS_OUTPUT_DIR)
    if(DEFINED MG_TARGET_DIR)
        set(apps_output_dir ${MG_TARGET_DIR})
    elseif(CMAKE_RUNTIME_OUTPUT_DIRECTORY)
        set(apps_output_dir ${CMAKE_RUNTIME_OUTPUT_DIRECTORY})
    else()
        set(apps_output_dir ${CMAKE_BINARY_DIR})
    endif()
    set(MG_APPS_OUTPUT_DIR ${apps_output_dir} CACHE INTERNAL "Where built apps land")
endif()

# The steps a gem and an app share. `kind` is GEM or APP and picks the argument
# prefix, the option namespace and the target flavour.
function(_mg_add_module kind name)
    string(TOLOWER ${kind} lower_kind)

    set(single_value OUTPUT_NAME)
    if(kind STREQUAL "APP")
        list(APPEND single_value GEM_FOLDER)
    endif()
    cmake_parse_arguments(MOD "DISABLED_BY_DEFAULT" "${single_value}"
            "SOURCES;INCLUDES;LINK;DEFINES;OPTIONS" ${ARGN})

    # A misspelled keyword would otherwise be swallowed as a source file and
    # reported much later as a missing file.
    if(MOD_UNPARSED_ARGUMENTS)
        message(FATAL_ERROR "mg_add_${lower_kind}(${name}): unrecognized "
                "argument(s) '${MOD_UNPARSED_ARGUMENTS}'.")
    endif()

    # Set by MagicCompiler at the top level. Missing means this module was
    # included from a tree that never ran it, and this target would get to break
    # the rules the rest of the tree is held to.
    if(NOT DEFINED MG_C_WARNINGS)
        message(FATAL_ERROR "mg_add_${lower_kind}(${name}): MG_C_WARNINGS "
                "is not set; include(MagicCompiler) at the top level first.")
    endif()

    # One switch per target, so one that will not build on this machine is a -D
    # away from being left out rather than a directory to comment out.
    string(TOUPPER ${name} upper_name)
    if(MOD_DISABLED_BY_DEFAULT)
        set(default OFF)
    else()
        set(default ON)
    endif()
    option(MG_${kind}_${upper_name} "Build the ${name} ${lower_kind}" ${default})
    if(NOT MG_${kind}_${upper_name})
        message(STATUS "${kind} '${name}': disabled (MG_${kind}_${upper_name}=OFF)")
        return()
    endif()

    # Globbed unless SOURCES says otherwise. SOURCES is for the target that has
    # to be selective, like a backend split one file per platform.
    if(NOT MOD_SOURCES)
        file(GLOB_RECURSE MOD_SOURCES CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/*.c)
    endif()
    if(NOT MOD_SOURCES)
        message(FATAL_ERROR "${kind} '${name}': no sources found in ${CMAKE_CURRENT_SOURCE_DIR}")
    endif()

    # Absolute up front: source_group(TREE) below rejects any path it cannot see
    # as living under the directory it is given.
    set(sources "")
    foreach(source IN LISTS MOD_SOURCES)
        get_filename_component(source ${source} ABSOLUTE BASE_DIR ${CMAKE_CURRENT_SOURCE_DIR})
        list(APPEND sources ${source})
    endforeach()

    if(NOT MOD_OUTPUT_NAME)
        set(MOD_OUTPUT_NAME ${name})
    endif()

    set(target mg_${lower_kind}_${name})

    if(kind STREQUAL "GEM")
        # MODULE, not SHARED: a gem is opened at run time and never linked
        # against, so it wants no place on anyone's link line. PREFIX is cleared
        # so the file is <name>.so and not lib<name>.so -- the loader that
        # enumerates the folder should not have to know a naming convention.
        add_library(${target} MODULE ${sources})
        set_target_properties(${target} PROPERTIES
                PREFIX ""
                LIBRARY_OUTPUT_DIRECTORY ${MG_GEMS_OUTPUT_DIR}
                ARCHIVE_OUTPUT_DIRECTORY ${MG_GEMS_OUTPUT_DIR}
                PDB_OUTPUT_DIRECTORY ${MG_GEMS_OUTPUT_DIR}
        )
        message(STATUS "Gem '${name}': ${MG_GEMS_OUTPUT_DIR}/${MOD_OUTPUT_NAME}")
    else()
        add_executable(${target} ${sources})
        set_target_properties(${target} PROPERTIES
                RUNTIME_OUTPUT_DIRECTORY ${MG_APPS_OUTPUT_DIR}
                PDB_OUTPUT_DIRECTORY ${MG_APPS_OUTPUT_DIR}
        )

        # Where this app looks for gems: the gems' own output folder unless the
        # caller says otherwise. Baked in as defaults the app's command line is
        # expected to override; a development convenience, not a shipping
        # mechanism.
        if(NOT MOD_GEM_FOLDER)
            set(MOD_GEM_FOLDER ${MG_GEMS_OUTPUT_DIR})
        endif()
        list(APPEND MOD_DEFINES
                MG_APP_NAME="${name}"
                MG_APP_GEM_FOLDER="${MOD_GEM_FOLDER}")

        # An app is only as runnable as the gems it loads, so building one
        # builds them. Guarded: no gems declared yet is a legitimate build.
        if(TARGET mg_gems)
            add_dependencies(${target} mg_gems)
        endif()
        message(STATUS "App '${name}': ${MG_APPS_OUTPUT_DIR}/${MOD_OUTPUT_NAME}, "
                "gems from ${MOD_GEM_FOLDER}")
    endif()

    # The engine, and whatever else this target needs. PRIVATE because nothing
    # links a MODULE or an executable.
    target_link_libraries(${target} PRIVATE Magic::magic ${MOD_LINK})

    # The callback stub, when the gate built one: the main() that collects the
    # app's mg_on_* definitions and hands them to mg_app_run as pointers. Free
    # to link unconditionally -- its objects are only pulled in to resolve a
    # missing main, so an app that defines its own never draws them in.
    if(kind STREQUAL "APP" AND TARGET Magic::main)
        target_link_libraries(${target} PRIVATE Magic::main)
    endif()

    # c_std_11 arrives PUBLIC with Magic::magic; C_EXTENSIONS is a target
    # property and does not travel, so it is repeated here. Hidden visibility is
    # what makes MG_LIB_EXPORT the only thing that exports from a gem, and
    # means an app exports nothing.
    set_target_properties(${target} PROPERTIES
            C_EXTENSIONS ON
            C_VISIBILITY_PRESET hidden
            OUTPUT_NAME ${MOD_OUTPUT_NAME}
    )

    target_compile_options(${target} PRIVATE ${MG_C_WARNINGS} ${MOD_OPTIONS})

    # The log gates and the platform macro arrive as PUBLIC definitions on
    # Magic::magic; only the build flavour and the caller's defines are added.
    target_compile_definitions(${target} PRIVATE
            $<IF:$<CONFIG:Debug>,DEBUG_MAGIC,RELEASE_MAGIC>
            ${MOD_DEFINES}
    )

    # The target's own directory, so private headers are included by plain name.
    target_include_directories(${target} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${MOD_INCLUDES})

    # The engine's precompiled prelude, compiled per target: a gem's flags and
    # definitions differ from the engine's (no COMPILING_MAGIC), so REUSE_FROM
    # would hand it a PCH the compiler rejects. MG_CORE_PCH is only defined when
    # core/ was added in this tree; a consumer of an installed engine goes without.
    if(MG_PCH AND MG_CORE_PCH)
        target_precompile_headers(${target} PRIVATE ${MG_CORE_PCH})
    endif()

    # A handle for "build the gems/apps and nothing else". Created on first use;
    # no ALL, because each target is already part of the default build.
    set(group mg_${lower_kind}s)
    if(NOT TARGET ${group})
        add_custom_target(${group} COMMENT "Building Magic ${lower_kind}s")
    endif()
    add_dependencies(${group} ${target})

    # Mirror the on-disk layout in IDE project trees, as core/ and tests/ do.
    source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} PREFIX ${lower_kind}s/${name} FILES ${sources})
endfunction()

# mg_add_gem(<name>
#     [DISABLED_BY_DEFAULT] [OUTPUT_NAME <file-stem>]
#     [SOURCES <file>...] [INCLUDES <dir>...] [LINK <target-or-library>...]
#     [DEFINES <macro>...] [OPTIONS <compile-flag>...]
# )
#
# Declares the gem <name>, built from the .c files next to the calling
# CMakeLists unless SOURCES says otherwise, gated behind a MG_GEM_<NAME>
# option.
#
#     mg_add_gem(sdl)
#     mg_add_gem(vulkan LINK Vulkan::Vulkan DEFINES VK_NO_PROTOTYPES)
function(mg_add_gem name)
    _mg_add_module(GEM ${name} ${ARGN})
endfunction()

# mg_add_app(<name> ...) -- the same arguments as mg_add_gem, plus
# [GEM_FOLDER <dir>] to override where the app looks for gems. Gated behind a
# MG_APP_<NAME> option.
#
#     mg_add_app(sandbox)
#     mg_add_app(editor LINK ImGui::ImGui GEM_FOLDER ${CMAKE_BINARY_DIR}/editor_gems)
function(mg_add_app name)
    _mg_add_module(APP ${name} ${ARGN})
endfunction()

# Generates bindings/cpp/magic.h and the Magic::cpp target that carries it.
#
# Magic is a C library. A C++ consumer that includes one of its headers directly
# declares every function with C++ linkage, and the mangled symbols that
# produces are not the ones the library exports: it compiles, then fails at
# link. The umbrella applies extern "C" once, over every public header, so there
# is one right way to include Magic from C++ and it is the easy one.
#
# Generated from a glob rather than written, because the failure mode of a
# hand-maintained umbrella is a header somebody forgot to add. It lands in the
# source tree (bindings/cpp is a stable include path) and is meant to be
# committed.

include_guard(GLOBAL)

# mg_add_cpp_umbrella(HELD_OUT <header>...)
#
# Writes ${PROJECT_SOURCE_DIR}/bindings/cpp/magic.h over every header under the
# calling directory's include/, minus the HELD_OUT ones (spelled relative to
# include/, e.g. "magic/app.h"), and declares the Magic::cpp INTERFACE target a
# C++ consumer links to get the umbrella and the engine with it.
function(mg_add_cpp_umbrella)
    cmake_parse_arguments(ARG "" "" "HELD_OUT" ${ARGN})

    set(include_root "${CMAKE_CURRENT_SOURCE_DIR}/include")
    set(umbrella_dir "${PROJECT_SOURCE_DIR}/bindings/cpp")

    # Relative to the include root, which is exactly the spelling that goes into
    # the generated #include lines.
    file(GLOB_RECURSE headers CONFIGURE_DEPENDS
            RELATIVE "${include_root}"
            "${include_root}/*.h"
    )

    # A held-out entry that matches nothing is stale or misspelled, and without
    # this check it would silently drop a header from the public API.
    foreach(held IN LISTS ARG_HELD_OUT)
        if(NOT held IN_LIST headers)
            message(WARNING "mg_add_cpp_umbrella: held-out header '${held}' "
                    "does not exist under ${include_root}; remove it from HELD_OUT.")
        endif()
    endforeach()
    if(ARG_HELD_OUT)
        list(REMOVE_ITEM headers ${ARG_HELD_OUT})
    endif()
    list(SORT headers)

    # CONFIGURE_DEPENDS only re-runs the glob when a header appears or vanishes.
    # A header that merely starts using a new <system header> is a content
    # change, and the system include block below has to follow it -- so the
    # configure depends on the files themselves too.
    set(header_paths ${headers})
    list(TRANSFORM header_paths PREPEND "${include_root}/")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
            "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/magic.h.in" ${header_paths})

    # One pass builds both blocks: the umbrella's #include lines, and the
    # <system headers> they reach for. The system includes are hoisted ahead of
    # the extern "C" block in the template -- a C++ standard header expanded
    # inside extern "C" is ill-formed.
    set(umbrella_includes "")
    set(system_headers "")
    foreach(header IN LISTS headers)
        string(APPEND umbrella_includes "#include \"${header}\"\n")
        file(STRINGS "${include_root}/${header}" include_lines
                REGEX "^[ \t]*#[ \t]*include[ \t]*<[^>]+>")
        foreach(line IN LISTS include_lines)
            string(REGEX REPLACE "^[ \t]*#[ \t]*include[ \t]*<([^>]+)>.*$" "\\1"
                    system_header "${line}")
            list(APPEND system_headers "${system_header}")
        endforeach()
    endforeach()
    list(REMOVE_DUPLICATES system_headers)
    list(SORT system_headers)

    set(system_includes "")
    foreach(system_header IN LISTS system_headers)
        string(APPEND system_includes "#include <${system_header}>\n")
    endforeach()

    set(MG_UMBRELLA_INCLUDES "${umbrella_includes}")
    set(MG_UMBRELLA_SYSTEM_INCLUDES "${system_includes}")
    configure_file("${CMAKE_CURRENT_FUNCTION_LIST_DIR}/magic.h.in"
            "${umbrella_dir}/magic.h" @ONLY)

    list(LENGTH headers header_count)
    message(STATUS "Magic: wrote ${umbrella_dir}/magic.h over ${header_count} public headers.")

    # INTERFACE, not a library of its own: there is no C++ to compile, only a
    # header and the include path that finds it. The engine comes with it.
    add_library(mg_cpp INTERFACE)
    add_library(Magic::cpp ALIAS mg_cpp)
    target_link_libraries(mg_cpp INTERFACE magic)
    target_include_directories(mg_cpp INTERFACE
            $<BUILD_INTERFACE:${umbrella_dir}>
            $<INSTALL_INTERFACE:include>
    )
endfunction()

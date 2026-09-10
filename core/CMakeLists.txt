# The engine library itself. The warning set, the platform macro and the gates
# the headers read are resolved at the top level and inherited here, so this
# file only says what is true about core/.

file(GLOB_RECURSE MG_SOURCES CONFIGURE_DEPENDS
        "${CMAKE_CURRENT_SOURCE_DIR}/src/*.c"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/*.h"
        "${CMAKE_CURRENT_SOURCE_DIR}/include/*.h"
)

add_library(magic ${MG_SOURCES})

# The name the rest of the tree links: a namespaced alias makes a typo a
# configure error rather than a link line with a bare "-lMagic" nobody provides.
add_library(Magic::magic ALIAS magic)

# Hidden visibility: what leaves the library is what MG_EXPORT says leaves
# it and nothing else. ELF and Mach-O need asking; Windows exports nothing
# without __declspec in the first place.
set_target_properties(magic PROPERTIES
        OUTPUT_NAME magic
        C_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON
)

# Public headers are included as "magic/...", so the include root is
# core/include; consumers of an installed package find them in <prefix>/include.
target_include_directories(magic
        PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
        PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

# The precompiled prelude, injected ahead of every src/*.c. PRIVATE twice
# over: a build-time device of this target alone, and nothing about it travels
# to consumers. See src/pch.h for what belongs in it.
if (MG_PCH)
    target_precompile_headers(magic PRIVATE src/pch.h)
endif ()

# PUBLIC because c_std_11 has to travel: a gem or app compiled as C17 would get
# __VA_OPT__ and take different branches in asserts.h than the engine did.
target_compile_features(magic PUBLIC c_std_11)

# PUBLIC, all of it: a gem shares these structs by layout alone, and a consumer
# that saw different gates from the engine would compile out checks the engine
# compiled in. COMPILING_MAGIC is the one exception -- true here and false in
# every consumer, which turns MG_EXPORT into dllexport vs dllimport.
target_compile_definitions(magic
        PUBLIC
        ${MG_PLATFORM_DEFINE}
        $<$<BOOL:${BUILD_SHARED_LIBS}>:MG_SHARED>
        PRIVATE
        COMPILING_MAGIC
        $<IF:$<CONFIG:Debug>,DEBUG_MAGIC,RELEASE_MAGIC>
        $<$<BOOL:${MG_HOT_RELOAD_GEMS}>:MG_HOT_RELOAD_GEMS>
)

# The gates, straight through: the option names are the macro names.
foreach (gate IN ITEMS MG_ASSERT_ENABLED MG_ERROR_ENABLED MG_WARNING_ENABLED
        MG_INFO_ENABLED MG_DEBUG_ENABLED MG_TRACE_ENABLED MG_USE_CALLBACKS
        MG_TRACK_MEMORY)
    target_compile_definitions(magic PUBLIC ${gate}=$<BOOL:${${gate}}>)
endforeach ()

target_compile_options(magic PRIVATE ${MG_C_WARNINGS})

# dlopen lives in libdl on older glibc; CMAKE_DL_LIBS is empty everywhere that
# needs nothing, so this is only ever the Linux link fix it exists to be.
target_link_libraries(magic PRIVATE ${CMAKE_DL_LIBS})

if (WIN32)
    target_compile_definitions(magic PRIVATE WIN32_LEAN_AND_MEAN VC_EXTRALEAN)
endif ()

# Mirror the on-disk layout in IDE project trees.
source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} PREFIX core FILES ${MG_SOURCES})

# ==============================================================================
# main/main.c -- the callback stub, and the Magic::main target
# ==============================================================================
# STATIC always, even when the engine is shared -- especially then: being
# compiled into the app's own binary is the whole mechanism. The stub's
# references to the five mg_on_* names resolve at the app's link, where the
# definitions live, and only pointers cross into the engine; see main/main.c.
# mg_add_app links it into every app, and a static library's objects are only
# pulled in to resolve a missing symbol, so an app that defines its own main()
# simply never draws the stub's in.
if (MG_USE_CALLBACKS)
    add_library(magic_main STATIC main/main.c)
    add_library(Magic::main ALIAS magic_main)

    # PUBLIC: linking the stub is opting into the engine, so the engine and its
    # definitions (the MG_USE_CALLBACKS the stub reads among them) come along.
    target_link_libraries(magic_main PUBLIC Magic::magic)

    target_compile_options(magic_main PRIVATE ${MG_C_WARNINGS})
    source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} PREFIX core FILES main/main.c)
endif ()

# ==============================================================================
# bindings/cpp/magic.h -- the C++ umbrella, and the Magic::cpp target
# ==============================================================================
# Held out and why -- finishing one of these deletes its line here:
#   app.h           includes events/event.h and files/dirs.h, not in the tree yet.
include(MagicUmbrella)
mg_add_cpp_umbrella(HELD_OUT
        magic/app.h
)

# Resolves MG_C_WARNINGS, the warning set every Magic C target is built with.
# Included from the top level rather than from core/, because tests/ is a
# sibling directory and would not otherwise inherit the variable. Reads
# MG_WERROR, so the option must be declared before this module is included.
#
# Magic compiles under any C11 compiler; this is a spelling table, not a support
# list. The only question it answers is how this compiler spells its warnings.
# clang-cl defines _MSC_VER but is still clang and takes GNU-style -W flags, so
# it belongs on the clang branch.

include_guard(GLOBAL)

include(CheckCCompilerFlag)

if(CMAKE_C_COMPILER_ID MATCHES "^(Apple)?Clang$|^GNU$")
    # ==========================================================================
    # GNU-style: clang and gcc
    # ==========================================================================
    # The shared list first: every flag here is spelled the same way and means
    # the same thing in clang and gcc, floor gcc 8 / clang 9. Both compilers
    # hard-error on an unknown -W option, so anything newer needs a
    # check_c_compiler_flag guard.
    set(MG_C_WARNINGS
            -Wall -Wextra -Wpedantic
            # Type and value conversions that silently lose information.
            -Wconversion -Wsign-conversion -Wfloat-equal -Wdouble-promotion
            -Wbad-function-cast -Wcast-qual -Wcast-align -Wpointer-arith
            # Declarations and prototypes: C's weakest link.
            -Wstrict-prototypes -Wmissing-prototypes -Wold-style-definition
            -Wnested-externs -Wredundant-decls -Wmissing-include-dirs
            # Control flow and initialization.
            -Wshadow -Winit-self -Wnull-dereference -Wimplicit-fallthrough
            -Wswitch-enum -Wswitch-default
            # Format strings, and constructs that are usually a mistake.
            -Wformat=2 -Wwrite-strings -Wundef -Wunused-macros
            -Wvla -Walloca -Wpacked -Wdate-time
    )

    # The aggressive tail is where the two diverge; neither list has an
    # equivalent in the other compiler.
    if(CMAKE_C_COMPILER_ID STREQUAL "GNU")
        list(APPEND MG_C_WARNINGS
                -Wlogical-op -Wduplicated-cond -Wduplicated-branches
                -Wjump-misses-init -Wtrampolines -Walloc-zero
                -Wformat-overflow=2 -Wformat-truncation=2 -Wstringop-overflow=4
                -Wshift-overflow=2 -Warray-bounds=2 -Wattribute-alias=2
        )
    else()
        list(APPEND MG_C_WARNINGS
                -Wcomma -Wassign-enum -Wconditional-uninitialized -Wloop-analysis
                -Wshift-sign-overflow -Wtautological-constant-in-range-compare
                -Wformat-type-confusion -Wunreachable-code-aggressive
                -Wextra-semi -Wnewline-eof
        )
    endif()

    if(MG_WERROR)
        # Unused code is work in progress, not a defect: -Wno-error demotes the
        # unused family back to warnings after -Werror promotes everything.
        list(APPEND MG_C_WARNINGS -Werror
                -Wno-error=unused-variable -Wno-error=unused-parameter
                -Wno-error=unused-function -Wno-error=unused-macros
                -Wno-error=unused-value -Wno-error=unused-label
                -Wno-error=unused-local-typedefs)
        # Newer than the floor (clang 13), so guarded like the rest; an unknown
        # -Wno-error= would itself warn, and -Werror would make that fatal.
        check_c_compiler_flag("-Wno-error=unused-but-set-variable"
                MG_HAVE_NO_ERROR_UNUSED_BUT_SET)
        if(MG_HAVE_NO_ERROR_UNUSED_BUT_SET)
            list(APPEND MG_C_WARNINGS -Wno-error=unused-but-set-variable)
        endif()
    endif()

elseif(CMAKE_C_COMPILER_ID STREQUAL "MSVC")
    # ==========================================================================
    # MSVC-style: cl.exe
    # ==========================================================================
    # /W4 is a level, not a set: many of the checks that matter most are off by
    # default at every level and have to be promoted by number with /w1NNNN.
    # cl.exe ignores warning numbers it has never issued, so the numbered flags
    # need no version guards; the structural switches are checked before use.
    set(MG_C_WARNINGS /W4)

    # Not a style preference -- Magic does not build without this one. The
    # argument-count dispatch in asserts.h and list.h expands __VA_ARGS__ into a
    # macro that counts what it was handed, and the traditional preprocessor
    # passes it along as a single token. MSVC 19.26+.
    check_c_compiler_flag("/Zc:preprocessor" MG_HAVE_ZC_PREPROCESSOR)
    if(MG_HAVE_ZC_PREPROCESSOR)
        list(APPEND MG_C_WARNINGS /Zc:preprocessor)
    else()
        message(FATAL_ERROR
                "Magic needs MSVC's conformant preprocessor "
                "(/Zc:preprocessor, MSVC 19.26+), which this cl.exe does not "
                "accept. Upgrade MSVC, or build with clang.")
    endif()

    # The nearest thing MSVC has to -Wpedantic: rejects the Microsoft language
    # extensions that would otherwise compile here and nowhere else.
    check_c_compiler_flag("/permissive-" MG_HAVE_PERMISSIVE_MINUS)
    if(MG_HAVE_PERMISSIVE_MINUS)
        list(APPEND MG_C_WARNINGS /permissive-)
    endif()

    list(APPEND MG_C_WARNINGS
            # Conversions that silently lose information (-Wconversion,
            # -Wsign-conversion). 4365 and 4388 catch signed/unsigned mixing.
            /w14242 /w14254 /w14287 /w14296 /w14365 /w14388 /w14826
            # Pointer and integer truncation: the 64-bit portability checks.
            /w14302 /w14311 /w14312
            # 4255 is -Wstrict-prototypes: fires on the '()' C reads as
            # "unspecified arguments" rather than "none".
            /w14255
            # Control flow. 4456-4459 are the four flavours of -Wshadow, 5262 is
            # -Wimplicit-fallthrough, 4061/4062 are -Wswitch-enum/-default.
            /w14456 /w14457 /w14458 /w14459 /w15262 /w14061 /w14062
            # Format strings: the part of -Wformat=2 MSVC can do unaided.
            /w14473 /w14474 /w14476 /w14477 /w14778
            # 4668 is -Wundef: every MG_*_ENABLED gate is passed on the
            # command line so a misspelled gate is an error, not a silent 0.
            /w14668
            # Usually a mistake. 4545-4549 are -Wcomma, 4555 is a discarded
            # expression, 4619 is a #pragma naming a nonexistent warning.
            /w14545 /w14546 /w14547 /w14549 /w14555 /w14619
            # Turned OFF: 4996 is the CRT nagging that fopen has _s variants --
            # an opinion about portable C, and fatal under /WX.
            /wd4996
    )

    if(MG_WERROR)
        # cl has no "keep it a warning" escape from /WX, so the unused family
        # (4100 parameter, 4101/4189 locals, 4505 static function) is turned
        # off outright rather than allowed to fail the build.
        list(APPEND MG_C_WARNINGS /WX /wd4100 /wd4101 /wd4189 /wd4505)
    endif()

else()
    # Defined and empty, deliberately: MagicTargets.cmake refuses to build a
    # target when MG_C_WARNINGS is not DEFINED, and an empty list says this
    # module ran and had nothing to offer this compiler.
    set(MG_C_WARNINGS "")
    message(STATUS
            "Magic: no warning set for CMAKE_C_COMPILER_ID "
            "'${CMAKE_C_COMPILER_ID}'; building without one. Add a branch to "
            "cmake/MagicCompiler.cmake to give this compiler diagnostics.")
endif()

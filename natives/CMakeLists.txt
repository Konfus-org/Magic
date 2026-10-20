# Every native library Magic binds, as one CMake superbuild: each is an ExternalProject that CMake fetches at its
# pinned tag, or downloads as the release archive its author publishes, builds in Release with this generator and
# takes the shared library of, into one folder per machine (MAGIC_NATIVES). A gem's project says only what it binds
# (its headers and names); this file says how each library is had, which is where the differences between platforms
# belong, so a new platform changes this file and no gem. README.md beside this file explains the whole.
cmake_minimum_required(VERSION 3.28)
project(MagicNatives NONE)
include(ExternalProject)

set(MAGIC_NATIVES "" CACHE PATH "The folder every library's source, build and product go under, shared by every checkout on the machine.")
set(MAGIC_RID "" CACHE STRING "The .NET runtime identifier being built for (win-x64, linux-x64, osx-arm64).")
if(NOT MAGIC_NATIVES OR NOT MAGIC_RID)
  message(FATAL_ERROR "MAGIC_NATIVES and MAGIC_RID are set by Directory.Build.targets, which is what runs this build.")
endif()
string(REGEX REPLACE ".*-" "" MAGIC_ARCH "${MAGIC_RID}")

# A library's folder: source/ is what was fetched, build/ CMake's working folder, built/ what it installed, and
# built/native/<file> the one shared library the build copies beside the gem. Every ExternalProject step runs the
# library's own build on CMAKE_BUILD_PARALLEL_LEVEL cores, which the caller sets, while this superbuild itself runs
# one library at a time, so the cores and the memory a build takes are bounded once.
#
# magic_library(<name> GIT <repository> <tag> | URL <url> <sha256>
#               [OPTIONS <cmake options>...] [TARGET <target>] [LIBRARY <path>] [SOURCE <folder>] [DEPENDS <names>...]
#               [FETCH_ONLY] [SHALLOW_SUBMODULES])
# The library is configured and installed unless TARGET names the one target to build instead, or FETCH_ONLY says it
# is fetched for its source alone. The shared library is found by its name under built/ then build/, or at LIBRARY,
# relative to source/, where an archive holds several. SOURCE builds from a CMake folder of this repository's own. A
# repository's submodules come with it, and theirs in turn unless SHALLOW_SUBMODULES stops there.
function(magic_library name)
  cmake_parse_arguments(ARG "FETCH_ONLY;SHALLOW_SUBMODULES" "TARGET;LIBRARY;SOURCE" "GIT;URL;OPTIONS;DEPENDS" ${ARGN})
  set(folder "${MAGIC_NATIVES}/${name}")
  set(fetch)
  if(ARG_GIT)
    list(GET ARG_GIT 0 repository)
    list(GET ARG_GIT 1 tag)
    set(fetch GIT_REPOSITORY "${repository}" GIT_TAG "${tag}" GIT_SHALLOW ON GIT_PROGRESS OFF UPDATE_DISCONNECTED ON)
    if(ARG_SHALLOW_SUBMODULES)
      list(APPEND fetch GIT_SUBMODULES_RECURSE OFF)
    endif()
  elseif(ARG_URL)
    list(GET ARG_URL 0 url)
    list(GET ARG_URL 1 sha256)
    set(fetch URL "${url}" URL_HASH "SHA256=${sha256}")
  endif()
  if(ARG_SOURCE)
    set(fetch SOURCE_DIR "${ARG_SOURCE}")
  else()
    list(APPEND fetch SOURCE_DIR "${folder}/source")
  endif()

  # An empty command, which turns a step off, must be written in the call itself: a list drops empty elements.
  set(common ${fetch}
    PREFIX "${folder}" BINARY_DIR "${folder}/build" INSTALL_DIR "${folder}/built"
    STAMP_DIR "${folder}/stamp" TMP_DIR "${folder}/tmp" DOWNLOAD_DIR "${folder}/download"
    CMAKE_ARGS -DCMAKE_BUILD_TYPE=Release "-DCMAKE_INSTALL_PREFIX=${folder}/built" ${ARG_OPTIONS})
  if(ARG_DEPENDS)
    list(APPEND common DEPENDS ${ARG_DEPENDS})
  endif()
  if(ARG_FETCH_ONLY OR ARG_URL)
    ExternalProject_Add(${name} ${common} CONFIGURE_COMMAND "" BUILD_COMMAND "" INSTALL_COMMAND "")
  elseif(ARG_TARGET)
    ExternalProject_Add(${name} ${common} BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --target ${ARG_TARGET} INSTALL_COMMAND "")
  else()
    ExternalProject_Add(${name} ${common})
  endif()

  if(NOT ARG_FETCH_ONLY)
    set(file "${CMAKE_SHARED_LIBRARY_PREFIX}${name}${CMAKE_SHARED_LIBRARY_SUFFIX}")
    ExternalProject_Add_Step(${name} take
      COMMENT "Taking ${file} from ${name}"
      COMMAND ${CMAKE_COMMAND} "-DFOLDER=${folder}" "-DFILE=${file}" "-DPATH=${ARG_LIBRARY}" -P "${CMAKE_CURRENT_SOURCE_DIR}/take.cmake"
      DEPENDEES install)
  endif()
endfunction()

# The platform library: installed, so the two built on it find it.
magic_library(SDL3 GIT https://github.com/libsdl-org/SDL release-3.4.18
  OPTIONS -DSDL_TEST_LIBRARY=OFF -DSDL_EXAMPLES=OFF -DSDL_INSTALL_TESTS=OFF)
# SDL_image reads its formats itself, so none of its vendored codecs is built, nor the submodules under theirs
# fetched; SDL_ttf brings FreeType and HarfBuzz with it. Each is its shared library alone.
magic_library(SDL3_image GIT https://github.com/libsdl-org/SDL_image release-3.4.8 DEPENDS SDL3 TARGET SDL3_image-shared SHALLOW_SUBMODULES
  OPTIONS -DSDLIMAGE_VENDORED=OFF -DSDLIMAGE_SAMPLES=OFF -DSDLIMAGE_AVIF=OFF -DSDLIMAGE_WEBP=OFF -DSDLIMAGE_TIF=OFF -DSDLIMAGE_JXL=OFF
          "-DCMAKE_PREFIX_PATH=${MAGIC_NATIVES}/SDL3/built")
magic_library(SDL3_ttf GIT https://github.com/libsdl-org/SDL_ttf release-3.2.2 DEPENDS SDL3 TARGET SDL3_ttf-shared
  OPTIONS -DSDLTTF_VENDORED=ON -DSDLTTF_SAMPLES=OFF -DSDLTTF_PLUTOSVG=OFF "-DCMAKE_PREFIX_PATH=${MAGIC_NATIVES}/SDL3/built")

# Assimp: the importers of the formats Model lists, no exporters, tools or tests, named plainly rather than by
# compiler. It builds its own zlib, so the library stands alone, except on macOS, where it links the OS's: the bundled
# copy's zutil.h defines fdopen away on Apple platforms, which breaks the declaration in the current SDK's stdio.h.
if(APPLE)
  set(assimp_zlib OFF)
else()
  set(assimp_zlib ON)
endif()
magic_library(assimp GIT https://github.com/assimp/assimp v6.0.5 TARGET assimp
  OPTIONS -DASSIMP_BUILD_TESTS=OFF -DASSIMP_BUILD_ASSIMP_TOOLS=OFF -DASSIMP_INSTALL=OFF -DASSIMP_BUILD_ZLIB=${assimp_zlib}
          -DASSIMP_WARNINGS_AS_ERRORS=OFF -DASSIMP_INJECT_DEBUG_POSTFIX=OFF -DLIBRARY_SUFFIX= -DASSIMP_NO_EXPORT=ON
          -DASSIMP_BUILD_ALL_IMPORTERS_BY_DEFAULT=OFF -DASSIMP_BUILD_FBX_IMPORTER=ON -DASSIMP_BUILD_GLTF_IMPORTER=ON -DASSIMP_BUILD_OBJ_IMPORTER=ON)

# Box3D as a shared library; its samples, tests and heavy validation are left out.
magic_library(box3d GIT https://github.com/erincatto/box3d v0.1.0
  OPTIONS -DBUILD_SHARED_LIBS=ON -DBOX3D_SAMPLES=OFF -DBOX3D_UNIT_TESTS=OFF -DBOX3D_VALIDATE=OFF)

# DirectXShaderCompiler: Microsoft's own build of the tag where it publishes one, since the library is an LLVM and the
# one build that takes long; built here from source where it does not (macOS, and Linux on ARM), with Microsoft's
# predefined parameters, SPIR-V code generation and no tests. Only the compiler's library is taken: the gem binds its
# COM API by hand, and validates and signs DXIL through the compiler itself.
set(dxc_tag v1.9.2609)
if(WIN32)
  magic_library(dxcompiler URL https://github.com/microsoft/DirectXShaderCompiler/releases/download/${dxc_tag}/dxc_2026_09_29.zip
    ad31b1fc8443175d204f77a611fdb3ef2ec42759bdc2f1167368de24a4a7e7f1 LIBRARY bin/${MAGIC_ARCH}/dxcompiler.dll)
elseif(LINUX AND MAGIC_ARCH STREQUAL "x64")
  magic_library(dxcompiler URL https://github.com/microsoft/DirectXShaderCompiler/releases/download/${dxc_tag}/linux_dxc_2026_09_28.x86_x64.tar.gz
    96faadc7f5c282d2ffda49804beb4c3ee38127bc252b723234e3c5cdf7aa39a1 LIBRARY lib/libdxcompiler.so)
else()
  magic_library(dxcompiler GIT https://github.com/microsoft/DirectXShaderCompiler ${dxc_tag} TARGET dxcompiler
    OPTIONS -C <SOURCE_DIR>/cmake/caches/PredefinedParams.cmake -DHLSL_INCLUDE_TESTS=OFF -DSPIRV_BUILD_TESTS=OFF
            -DLLVM_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_TESTS=OFF -DLLVM_ENABLE_ASSERTIONS=OFF -DHLSL_BUILD_DXILCONV=OFF -DLLVM_APPEND_VC_REV=OFF)
endif()

# SPIRV-Cross's C API as a shared library, for the Metal shaders the gem translates.
magic_library(spirv-cross-c-shared GIT https://github.com/KhronosGroup/SPIRV-Cross vulkan-sdk-1.4.363.0
  OPTIONS -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF)

# Dear ImGui, through the C API that dear_bindings, the Dear ImGui team's generator, makes of its headers here, since
# it ships none: the public API and the internal one, in their own files, generated into dear_bindings' own checkout
# (which the gem reads its headers from), then built with ImGui's sources into one library by the CMake file under
# dcimgui/. The generator is Python 3 with the ply package.
find_package(Python3 REQUIRED COMPONENTS Interpreter)
magic_library(imgui GIT https://github.com/ocornut/imgui v1.92.9b-docking FETCH_ONLY)
set(imgui "${MAGIC_NATIVES}/imgui/source")
# dear_bindings is fetched into dcimgui's source folder, where the gem reads the generated headers; its own stamps keep
# apart from the library's. The generation runs again when ImGui was fetched afresh.
set(bindings "${MAGIC_NATIVES}/dcimgui/source")
ExternalProject_Add(dear_bindings
  GIT_REPOSITORY https://github.com/dearimgui/dear_bindings GIT_TAG v0.24 GIT_SHALLOW ON GIT_PROGRESS OFF UPDATE_DISCONNECTED ON
  PREFIX "${MAGIC_NATIVES}/dear_bindings" SOURCE_DIR "${bindings}"
  CONFIGURE_COMMAND "" BUILD_COMMAND "" INSTALL_COMMAND ""
  DEPENDS imgui)
ExternalProject_Add_Step(dear_bindings generate
  COMMENT "Generating Dear ImGui's C API"
  COMMAND ${Python3_EXECUTABLE} "${bindings}/dear_bindings.py" -o "${bindings}/dcimgui" "${imgui}/imgui.h"
  COMMAND ${Python3_EXECUTABLE} "${bindings}/dear_bindings.py" -o "${bindings}/dcimgui_internal" --include "${imgui}/imgui.h" "${imgui}/imgui_internal.h"
  DEPENDEES download DEPENDS "${MAGIC_NATIVES}/imgui/stamp/imgui-done")
magic_library(dcimgui SOURCE "${CMAKE_CURRENT_SOURCE_DIR}/dcimgui" DEPENDS imgui dear_bindings TARGET dcimgui
  OPTIONS "-DIMGUI_DIR=${imgui}" "-DGENERATED_DIR=${bindings}")

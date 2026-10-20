# Dear ImGui and its C API as one shared library: ImGui's own sources with the dcimgui sources dear_bindings generated
# from them, which the superbuild (../CMakeLists.txt) points at (IMGUI_DIR, GENERATED_DIR). The defines are the ones
# the gem reads the generated headers with (its project's Defines), so what is compiled is what is bound.
cmake_minimum_required(VERSION 3.16)
project(dcimgui CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(dcimgui SHARED
  ${IMGUI_DIR}/imgui.cpp
  ${IMGUI_DIR}/imgui_draw.cpp
  ${IMGUI_DIR}/imgui_tables.cpp
  ${IMGUI_DIR}/imgui_widgets.cpp
  ${IMGUI_DIR}/imgui_demo.cpp
  ${GENERATED_DIR}/dcimgui.cpp
  ${GENERATED_DIR}/dcimgui_internal.cpp)
target_include_directories(dcimgui PRIVATE ${IMGUI_DIR} ${GENERATED_DIR})
target_compile_definitions(dcimgui PRIVATE IMGUI_USE_WCHAR32 IMGUI_DISABLE_OBSOLETE_FUNCTIONS=1)
if(WIN32)
  target_compile_definitions(dcimgui PRIVATE "CIMGUI_API=__declspec(dllexport)")
endif()

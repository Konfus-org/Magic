# ImGuiDebugUI

The debug UI, drawn with [Dear ImGui](https://github.com/ocornut/imgui): provides `IDebugUI` and `IOverlay`.

It shows what gems and scripts report: lines down the left of the picture and short text at places in the world. Info,
warning and error reports are logged too. Other gems draw their debug windows with its widgets.

To use it, list `ImGuiDebugUI` in a project's `.magic` file, with the GPU provider, the shader compiler and the ECS. A
gem that takes `IDebugUI` is made after it.

- The renderer draws the UI over the finished picture, so it shows in screenshots too.
- Reports always show in a Debug build. In other builds they show only while a debug window is open.
- Without a gem that provides `IInput`, nothing can be clicked or typed. Without one that provides `IClipboard`, nothing
  can be copied or pasted.
- Nothing is drawn until the UI's shaders compile and the renderer draws its first frame, and nothing without a
  renderer.
- Text is written in ImGui's own font.

Dear ImGui is a native library, used through the C API that
[dear_bindings](https://github.com/dearimgui/dear_bindings) generates
([Native libraries](../README.md#native-libraries)).

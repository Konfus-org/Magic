# ImGuiDebugUI

The debug UI, drawn with [Dear ImGui](https://github.com/ocornut/imgui). Provides `IDebugUI`
([The debug UI](../../../Core/README.md#the-debug-ui)).

It shows what gems and scripts report: lines down the left of the window and short text at places in the world.
Info, warning and error reports are logged too. Other gems draw their debug windows with its widgets.

A renderer draws the UI over the finished frame by running the pipeline script in the gem's resources. Its settings,
such as the font and its size, are on `ImGuiDebugUISettings` and show in the settings window (F4). Dear ImGui is a
native library, used through the C API that [dear_bindings](https://github.com/dearimgui/dear_bindings) generates
([Native libraries](../README.md#native-libraries)).

- Reports always show in a Debug build. In other builds they show only while a debug window is open.
- Without an input gem, nothing can be clicked or typed.
- Nothing is drawn until the UI's shaders compile.

# VulkanGpu

The GPU, through Vulkan 1.3. Provides `IGpu`. Vulkan's API comes from
[Vortice.Vulkan](https://github.com/amerkoleci/Vortice.Vulkan), over the system's Vulkan loader.

It draws into the platform gem's windows. List that gem before this one in the project's `.magic` file. Its settings,
such as the GPU to open, vsync, HDR composition and Vulkan's validation layer, are on `VulkanGpuSettings` and show in
the settings window (F4). Changing the GPU or validation restarts the engine. Validation needs the Vulkan SDK, and its
messages go to the log.

It measures upload sizes, how long the render thread waits for a window's next image and, while something listens,
the GPU time of each renderer pass ([Telemetry](../../../Core/README.md#telemetry)).

- Call it from the render thread only.
- It needs a GPU with Vulkan 1.3, dynamic rendering, synchronization2, timeline semaphores, multi-draw indirect and
  depth clamping. Without one, it fails to start.
- Screenshots fail under an HDR composition.
- Headless, there is no window image, but it still renders offscreen.

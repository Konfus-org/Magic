# VulkanGraphics

The GPU through Vulkan 1.3: provides `IGraphicsApi`. Resources go into one bindless heap, which every shader reads by
the id in a handle; a frame is one `GpuCommands` value, recorded by whatever draws and submitted here.

To use it, list `VulkanGraphics` in a project's `.magic` file after the gem that provides the window.

- The GPU is the fastest one with Vulkan 1.3, bindless descriptors and dynamic rendering that can show the window;
  the log names it as the run starts. Mesh shaders, ray queries and 64-bit atomics are noted in `GpuFeatures` when
  the GPU has them. With ray queries, acceleration structures join the heap: a bottom-level one over triangles of its
  buffers, a top-level one over instances of those, each built by a command of the frame.
- It draws into the window through a surface it makes from the window's OS handles, on Windows, X11, Wayland and macOS.
- It publishes each pass's and each frame's time on the GPU, and the device memory it holds and has, on its
  `Magic.Gems.VulkanGraphics` meter, whose tags name the Vulkan version, the GPU, its kind and its driver.
- A Debug build turns on the Khronos validation layer when the Vulkan SDK is installed, and logs what it reports.
- Headless, there is no window image: `WindowTarget` is none, and everything else works the same, on a software
  device such as Mesa's lavapipe where there is no GPU.
- Vulkan's API comes from the Vortice.Vulkan package over the system's Vulkan loader; nothing is bound here.

# VulkanGraphics

The GPU through Vulkan 1.3: provides `IGraphicsApi`. Resources go into one bindless heap, which every shader reads by
the id in a handle. A frame is one `GpuCommands` value, which the gems record into Core's graph and Core presents here;
it puts in the barriers each pass's uses need, from what each resource was last used as.

To use it, list `VulkanGraphics` in a project's `.magic` file after the gems that provide the window and the shader
compiler.

- A texture, buffer or top-level acceleration structure the graph names, or a sampler it asks for by description, is
  made on the first ask and kept while its name is asked for with the same description; another description makes it
  again. A buffer asked for at another size alone keeps what it holds, as much as fits, copied before the frame's
  passes. One no frame has asked for in a few frames is destroyed, so a gem asks every frame for what it reads.
- A compute pass the graph records with a shader entry runs that entry, compiled through the shader compiler and built
  on the first ask. A draw pass's vertex and pixel entries are compiled the same way, and built into a pipeline for the
  formats of the targets it draws into on its first draw. The pass is left out of frames until then; a shader that
  fails is logged, and its passes stay out.

- The GPU is the fastest one with Vulkan 1.3, bindless descriptors and dynamic rendering that can show the window;
  the log names it as the run starts. `Rendering.Gpu` picks another by a part of its name, and changing it restarts
  the run. Mesh shaders, ray queries and 64-bit atomics are noted in `GpuFeatures` when
  the GPU has them. With ray queries, acceleration structures join the heap: a bottom-level one over triangles of its
  buffers, a top-level one over instances of those, each built by a command of the frame. Once a bottom-level one's
  build has run, the next frame copies it into one of the size it compacts to, which its name then gives with a new
  id and address, and frees the built one with its scratch.
- It draws into the window through a surface it makes from the window's OS handles, on Windows, X11, Wayland and macOS.
  With `Window.IsVSyncOn` set, the window waits for the display, which caps the frame rate at the display's. It is
  off by default: each frame shows as soon as it is drawn, so the frame rate is what the frame costs, in immediate
  mode, which can tear, where the surface has it, else in mailbox mode. Setting it remakes the window's images. The
  window has as few images as its surface takes, and two at least, with one more in mailbox mode.
- Its resources and frames are the render thread's, and its members throw on any other.
- It publishes each pass's and each frame's time on the GPU, and the memory it holds and the GPU has, on its
  `Magic.Gems.VulkanGraphics` meter, whose tags name the Vulkan version, the GPU, its kind and its driver. The memory
  in use is what the driver says the process holds in the GPU's own heaps, the window's images and the driver's own
  memory included, where the driver has `VK_EXT_memory_budget`; elsewhere it is what the resources hold, and its
  `source` tag says which. The allocated gauge is what the resources hold either way.
- Each frame in flight uploads through a staging ring of host memory, which the allocated gauge counts. A ring grows for
  a big upload, such as a world's models arriving, and shrinks back a few seconds after uploads are small again.
- A Debug build turns on the Khronos validation layer when the Vulkan SDK is installed, and logs what it reports.
- Headless, there is no window image: `WindowTarget` is none, and everything else works the same, on a software
  device such as Mesa's lavapipe where there is no GPU.
- Vulkan's API comes from the Vortice.Vulkan package over the system's Vulkan loader; nothing is bound here.

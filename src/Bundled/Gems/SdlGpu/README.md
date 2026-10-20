# SdlGpu

The GPU, through SDL GPU, called through SDL's generated API ([Gems](../README.md#native-libraries)), which the SDL
bindings project provides: provides `IGpu` on Vulkan, Direct3D 12 or Metal. It draws through the SDL a platform gem
started, into its windows, so a project lists that gem before it; listed first, SdlGpu is not made and says so. SDL
comes from the bindings project, and the copy that runs is the one every gem shares
([Sharing a library](../README.md#sharing-a-library)), which the platform gem, bound first, loaded.

## Settings

Each setting is documented on `SdlGpuSettings` and shown in the settings window. Which GPU and API open, and whether
the API validates, are read as the device opens, so changing one starts the engine again; `vsync` and `composition`
are followed every frame, and every window presents the new way from the next. `composition` decides a window's
image's format, and so how the last pass of a frame encodes its colour, which `IGpu.GetFormat` tells; no display
without HDR takes the two HDR compositions, which then fall back to SDR.

## Behaviour

- Everything is on the render thread, which made the window: SDL acquires a window's image only there, and uses a
  command buffer only on the thread that acquired it. A call from any other thread throws: what a worker made for the
  GPU reaches the render thread as data a gem uploads in its `Render`, or, where a flow must await a step there, through
  `Threads.InvokeAsync`.
- The device opens in the gem's constructor, which starts SDL's video subsystem for it (headless, the platform leaves
  it down) and stops it again in `Dispose`. It asks for SPIR-V, DXIL and Metal source, and takes the first the driver
  takes as `IGpu.ShaderFormat`. Vulkan is asked for 1.1, whose subgroup operations are HLSL's wave intrinsics. If no
  device opens, the gem throws with SDL's error and is not made.
- It knows no window until one is asked for. `IGpu.GetWindowTarget(window)` claims the window, found by `IWindow.Id` in
  the SDL every gem shares, presenting as `vsync` says, and hands back a texture that is its render target, the same
  one every call; any number of windows can be targets. Their images' format is what `composition` made it, 8-bit
  UNORM (SDR) unless asked otherwise, which `IGpu.GetFormat` tells, so the last pass of a frame encodes its own colour
  for it. `Release` gives a window back, once the frames in flight are done with it. Headless, a window target's
  format is `GpuFormat.Invalid`, it has no image ever, and the GPU still computes and renders offscreen.
- `Render`, after that of every gem that takes `IGpu`, submits the frame while the main thread runs the next one: the
  uploads queued since the last submit run first, in one copy pass, then the recorded commands, one SDL call each;
  then the window presents if they drew into it, waiting for the display there rather than on the main thread. A frame
  with nothing recorded and nothing uploaded submits nothing. Two frames are in flight.
- A copy between textures or buffers is a command like any other, run in the frame's order in a copy pass of its own,
  so it sees what the commands before it wrote, as does making a texture's mips. Debug groups name stretches of the
  frame in a GPU capture.
- A window's image is acquired the first time a frame draws into it; minimised, there is none, and the passes
  into it are skipped. It can be drawn into, never sampled or read back, which not every API allows. `CaptureAsync`
  gets what it shows another way: the next frame that draws into the window draws into a texture of its own in its
  place, which is copied into the window once the frame's commands have run and read back, the render thread waiting
  for that frame once. Only an 8-bit image is read back: under an HDR composition the capture fails and says so.
- A released object stays valid until the frame's submit, and its SDL object goes once the GPU is done with every
  frame that could use it. What is still alive is released when the gem is disposed, windows given back included,
  before the platform gem closes them and SDL.
- `Read` waits for the GPU, holding the render thread: for checks, screenshots and tools, never every frame.

# SdlGpu

The GPU, through SDL GPU, called through SDL's generated API ([Gems](../README.md#native-libraries)), which the SDL bindings gem provides: provides
`IGpu` on Vulkan, Direct3D 12 or
Metal. It draws through the SDL a platform gem started, into its windows, so a project lists that gem before it;
listed first, SdlGpu is not made and says so.

## Settings

In the project's `Presets/Default.preset`, by another preset, or `--set SdlGpu.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `driver` | `auto` | The graphics API: `vulkan`, `direct3d12` or `metal`; `auto` for SDL's choice. The `SDL_GPU_DRIVER` environment variable still wins. Changed while running, the engine starts again. |
| `lowPower` | `false` | Open the GPU that saves power, the integrated one on a machine with two, rather than the fastest: `--set SdlGpu.lowPower=true` to try a slow machine's GPU on a fast one. SDL picks between GPUs only this way, never by name; the log names the one it opened. Changed while running, the engine starts again. |
| `vsync` | `true` in a release, `false` otherwise | Present on the display's refresh. Off, a frame is shown as soon as it is done (immediate, else mailbox), so a Debug or Optimize build shows its true frame rate. |
| `composition` | `sdr` | How a window's image is composed onto the display, which decides the image's format, and so how the last pass of a frame encodes its colour: `sdr` (8-bit UNORM, encoded to sRGB by the frame), `sdr_linear` (8-bit sRGB-typed, encoded by the GPU), `hdr10` (10-bit, ST 2084 on Rec. 2020) or `hdr_linear` (half floats, linear scRGB). A window that does not support the one asked for, as no display without HDR does the two `hdr` ones, is composed `sdr`, and a warning says so. Changed while running, every window is composed the new way, its image in the new format, from the next frame. |
| `validation` | `false` | The API's own validation (Vulkan's layers, Direct3D 12's debug layer): slow, and loud about misuse. The layers must be installed. Changed while running, the engine starts again. |

All but `vsync` and `composition` are read as the device opens, so they change nothing while running; those two are
followed every frame, and every window presents the new way from the next.

## Behaviour

- Everything is on the render thread, which made the window: SDL acquires a window's image only there, and uses a
  command buffer only on the thread that acquired it. A call from any other thread throws; a loader hands its work to
  the render thread (`Threads.InvokeAsync`).
- The device opens in the gem's constructor, which starts SDL's video subsystem for it (headless, the platform leaves
  it down) and stops it again in `Dispose`. It asks for SPIR-V, DXIL and Metal source, and takes the first the driver
  takes as `IGpu.ShaderFormat`. Vulkan is asked for 1.1, whose subgroup operations are HLSL's wave intrinsics. If no
  device opens, the gem throws with SDL's error and is not made.
- It knows no window until one is asked for. `IGpu.WindowTarget(window)` claims the window, found by `IWindow.Id` in the SDL
  every gem shares, presenting as `vsync` says, and hands back a texture that is its render target, the same one every
  call; any number of windows can be targets. Their images' format is what `composition` made it, 8-bit UNORM (SDR)
  unless asked otherwise, which `IGpu.FormatOf` tells, so the last pass of a frame encodes its own colour for it.
  `Release` gives a window back, once the frames in flight are done with it. Headless, a window target is
  `Invalid` and 0 x 0, and the GPU still computes and renders offscreen.
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

## Natives

The SDL bindings gem carries SDL for this machine, and every gem that references it copies it beside itself, but the SDL that runs is the
platform gem's copy, shared by name (`GemLoadContext`): the first gem to bind a library loads it for all.

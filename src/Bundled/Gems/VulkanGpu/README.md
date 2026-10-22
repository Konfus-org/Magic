# VulkanGpu

The GPU, through Vulkan 1.3: provides `IGpu`. Vulkan's API comes from Vortice.Vulkan, generated from Khronos's
registry over the system's Vulkan loader, which the GPU's driver installs; the windows are the platform gem's, made
Vulkan's surfaces through SDL, so a project lists that gem before this one, and listed first, this one is not made and
says so.

## Settings

Each setting is documented on `VulkanGpuSettings` and shown in the settings window. Which GPU opens, and whether
Vulkan's validation layer is on, are read as the device opens, so changing one starts the engine again; vsync and the
composition are followed every frame, and every window presents the new way from the next. The composition decides a
window's image's format, and so how the last pass of a frame encodes its colour, which `IGpu` tells of any target; a
display that cannot show the one asked for is composed SDR, with a warning.

## Behaviour

- **One thread.** Everything is on the render thread, which made the window: a call from any other thread throws. What
  a worker made for the GPU reaches the render thread as data a gem uploads in its `Render`, or, where a flow must
  await a step there, through `Threads`.
- **The device.** It opens in the gem's constructor, on the GPU the settings name, else the fastest, or the one that
  saves power when they say so, among those that run Vulkan 1.3 with dynamic rendering, synchronization2, timeline
  semaphores, multi-draw indirect and depth clamping: what every Vulkan the engine may run on has, MoltenVK's and
  KosmicKrisp's on a Mac among them. Without one, the gem throws and is not made. Shaders are SPIR-V, compiled for the
  binding model of the shaders' binding header: a descriptor set for each stage's reads, one for compute's writes, and
  one for each stage's constants.
- **Validation.** With the setting on, Vulkan's validation layer runs where it is installed, as the Vulkan SDK installs
  it, and the gem warns once where it is not. What it says goes to the log, an error as an error, so a run that fails on
  errors fails on one, and each kind once, as one mistake made every frame would bury the rest.
- **Frames.** Two are in flight, counted by one timeline semaphore: a frame slot is taken back once the GPU has finished
  the frame last drawn from it, with its command buffer, its descriptor sets and its staging. An empty frame submits
  nothing.
- **Uploads.** An upload is copied into the slot's mapped staging at the call, and from there into its buffer or texture
  at the start of the frame's submit, before every command, in call order; staging a frame outgrows is replaced by one
  twice the size, the old kept until the frame that copies from it is done.
- **Commands.** The frame's commands are plain data, recorded before the submit, so before a pass begins its commands
  say everything it touches: its targets, what each stage binds to read, what a compute pass writes, its vertex, index
  and argument buffers. One barrier, before the pass, waits for what last used each of them and puts each texture in
  the layout the pass uses it in; the pass's end is where what it wrote is done before anything after it uses it, as
  the renderer expects. A texture used whole, as one sampled is, is tracked as one, and level by level only while a pass
  uses one level alone. Each draw or dispatch writes the descriptor sets its pipeline reads from what the stages bound,
  into sets of the frame's, and names its stages' constants, kept in a ring of the frame's, by their offsets.
- **Clip space.** The viewport is flipped, so clip space is +Y up as on the other graphics APIs, and a pass starts with
  the viewport and the scissor over its whole target.
- **Release.** An object released leaves the tables at the next submit, and its Vulkan object goes once the GPU has
  finished every frame that could use it.
- **Windows.** Asking `IGpu` for a window's target makes the window a swapchain through SDL, presenting as the settings
  say, and hands back a texture that is its render target, the same one every call. Its image is acquired the first time
  a frame draws into it, the wait timed as `present.acquire.duration`, and presented after the submit. A window
  minimised has no image, and the passes into it are skipped; one resized, or presented to in a way its swapchain no
  longer matches, has its swapchain made again before its next image. Headless, a window target's format is invalid, it
  has no image ever, and the GPU still computes and renders offscreen.
- **Capture.** A capture copies the window's image after every command of the next frame that draws into it, and the
  render thread waits for that frame once to read it back. Only an 8-bit image is read back: under an HDR composition the
  capture fails and says so.
- **Reads.** A synchronous read waits for the GPU, holding the render thread: for checks, screenshots and tools, never
  every frame.
- **Memory.** Each buffer and texture has memory of its own, as the few hundred a renderer makes are far under the
  GPU's limit on allocations.

## Telemetry

The gem measures what it uploads, how long the render thread waits for a window's next image, and, while something
listens to them, the GPU's time on each top-level debug group of a frame and on the frame as a whole
([Core's README](../../../Core/README.md#telemetry)). A renderer's pass is a debug group, so the groups' times are its
passes' costs, by name. They are the GPU's own timestamps, written before and after each group and read when the
frame's slot comes round, so timing a frame changes nothing about how it runs and nothing waits for the times: a
group's time is its time in the frame as it is, overlapping what runs beside it.

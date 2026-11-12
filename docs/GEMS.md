# Gems

The plugins that come with Magic are in `src/Gems/`. A project runs one by listing its name in its `.magic` file. To
add one, see [Templates](TOOLS.md#templates).

## Gems that use other gems

A gem can use another gem's types. Reference its project with `Private="false"`, as with Core, so its assembly isn't
copied beside yours, and list both gems in the project. Magic loads the referenced gem first and gives your gem its one
copy, and refuses your gem, naming the other, when the project doesn't list it.

## Native libraries

A gem can call a C library. Declare it as a `NativeLibrary` item in the gem's project. The build fetches it, builds
it with CMake and generates its C# API with [ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp), which
the restore downloads. The item's metadata are documented in `Directory.Build.targets`.

- Built libraries are shared by every checkout on the machine, under `Magic/NativeLibs/` in local app data. Set
  `MagicNativeLibs` to move them and `MagicNativeCores` to limit the cores their build uses.
- The headers are read with the machine's own C headers. Call only what every platform declares.
- The API uses C's own names. Add `using static` for the library's class and the enums you use. Nothing is wrapped.
- A library several gems call goes in a project of its own, which they reference and no `.magic` file lists.
- A callback is an `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` method.
- List a declaration to leave out in the item's `Unbound`, with a comment saying why.
- To run the generator by hand: `ClangSharpPInvokeGenerator @<library>.bindings.rsp`, with the file the build writes
  in the project's intermediate folder.

## AssimpModelImporter

Reads FBX, glTF and OBJ files: provides `IAssetImporter<Model>`.

- A file's nodes are flattened into one model in the engine's frame: left-handed, +Y up, a metre a unit, triangles
  clockwise from outside, with normals and tangents made where the file has none.
- One mesh per material slot, slots numbered as the file's materials are first used, up to the slots a renderer has.
- The `.meta` file's `origin` is the point moved to the model's (0, 0, 0).

## SdlBindings

The C APIs of SDL3 and SDL_image, for the gems built on SDL, and `Sdl.Error`, SDL's reason for the last call that
failed. No project lists it in its `.magic` file. A gem that needs SDL references it in its csproj. The build builds
both libraries at the tags in the csproj ([Native libraries](#native-libraries)). Every gem that references it uses the
same SDL.

## SdlPlatform

The window, clipboard and input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow`, `IClipboard`
and `IInput`, and pumps the OS's events on the platform thread (`IPlatform`).

- Keyboards, mice and gamepads can be plugged in and out while running.
- Keys are physical keys (SDL scancodes), the same on every layout. Typed text follows the layout.
- Mouse position and motion are in window pixels, the motion scaled and eased as the settings say. The wheel is in
  notches.
- Gamepads use SDL's standard layout. Sticks go from -1 to 1 with up positive. Triggers go from 0 to 1.
- The window opens as `Window.Mode` says: fullscreen without a border in a Release build, and maximized in any other
  build, on the display `Window.Display` names. `set Window.Mode fullscreen|maximized|windowed` and
  `set Window.Display 1` change them while it runs.
- It paces the frames: `Window.MaxFrameRate` caps them while the window has the focus, and
  `Window.BackgroundFrameRate` while it hasn't, 0 for no cap. With `Simulation.PauseWhenUnfocused`, losing the focus
  pauses the world and getting it back resumes it. The wait shows as the gem's Update in the stats.
- The gem's settings are `SdlPlatformSettings`; `get Window`, `get Input` and `get Simulation` list them.
- The window's title is the project's name, and its icon the default one until `Icon` is set. Closing the window ends
  the run.
- The window and clipboard's members are the game thread's, and throw on any other; what is set reaches the OS on the
  platform thread's next pump. The window's size is read from any.
- Headless, it opens no window and needs no display. Only gamepads give input, and the clipboard's text stays in
  the gem.
- It publishes the RAM only this process holds, as `process.memory.usage` on its `Magic.Gems.SdlPlatform` meter: the
  private working set on Windows, the anonymous resident pages (`RssAnon`) on Linux, and the physical footprint on
  macOS, the numbers the OS's own tools show.

## SdlTextureImporter

Reads PNG, JPEG, TGA, BMP, SVG and ICO files into textures ready to upload, and keeps the result in the cache under the
file's version: provides `IAssetImporter<Texture>`. Writes a plain RGBA8 texture as a PNG: provides
`IAssetExporter<Texture>`.

- A file decodes, gets its mip chain when its `.meta` asks for one, and every level is packed into BC7 blocks, sRGB for
  colour and linear for a normal map or a mask, as the `.meta`'s usage says.
- A `.meta` with `isCompressed` off keeps plain RGBA8, for an image the CPU reads. An `.icon` file's image needs it, as
  a window's icon is plain pixels.
- An `.svg` is drawn at the width its `.meta`'s `size` says.

## SlangShaderCompiler

Compiles Slang entries to SPIR-V for the GPU provider: provides `IShaderCompiler`.

- A shader is a `.slang` asset under `Shaders/`; `import A.B;` is the asset at `Shaders/A/B.slang`. An entry's
  module and everything it imports load into one compile, so a change to any of them is a new compile.
- What compiles is cached under a hash of every module's text, in the project's cache, so the second run of an
  unchanged shader compiles nothing. What a struct's reflection finds is cached the same way. A change to the
  compiler's options or tag is a new cache version, which compiles everything once more.
- The setting `Debug.ShaderOptimization` sets how hard the compiler optimizes. Each level is cached apart, and a
  change restarts the run, so a level not used before compiles every shader once.
- The compiler starts with the first compile or reflection the cache doesn't hold, and stops after ten seconds with
  none, giving its memory back. A run whose shaders are all cached never starts it.
- Warnings are errors. Matrices are row-major. Entry points keep their names.
- The compiler's API is C++ interfaces, which the bindings generator cannot bind, so `Slang.cs` binds by hand the few
  calls the gem makes, by the indices `slang.h` gives them at the tag the project builds.

## VulkanGraphics

The GPU through Vulkan 1.3: provides `IGraphicsApi`. Resources go into one bindless heap, which every shader reads by
the id in a handle. A frame is one `GpuCommands` value, which the gems record into Core's graph and Core presents here;
it puts in the barriers each pass's uses need, from what each resource was last used as.

List it in a project's `.magic` file after the gems that provide the window and the shader compiler.

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
  the run. Ray queries are noted in `GpuFeatures` when the GPU has them. With ray queries, acceleration structures
  join the heap: a bottom-level one over triangles of its buffers, a top-level one over instances of those, each built
  by a command of the frame. Once a bottom-level one's build has run, the next frame copies it into one of the size it
  compacts to, which its name then gives with a new id and address, and frees the built one with its scratch.
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

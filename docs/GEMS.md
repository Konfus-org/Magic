# Gems

A gem is a plugin: a part of the engine that a project adds or replaces. The gems that come with Magic are in
`src/Gems/`. Each gem builds to `Gems/<Name>/` next to Magic. A project runs a gem when its `.magic` file lists the
name of the gem. To make a gem, use the gem template ([Templates](TOOLS.md#templates)).

## Gems that use other gems

A gem can use the types of another gem. To do this:

1. In the csproj of your gem, reference the project of the other gem with `Private="false"`, as you do for Core.
2. List both gems in the `.magic` file of the project.

With `Private="false"`, the build does not copy the assembly of the other gem next to yours. Magic loads the other gem
first and gives your gem that copy. If the project does not list the other gem, Magic refuses your gem and names the
other gem.

## Console commands

A gem can add to Magic's console what only that gem knows. To do this, take `ConsoleCommands` in the constructor of
the entry point of the gem.

To let a tool read values, register a subject of `query`. The function gives the values as a JSON object:

```csharp
commands.RegisterQuery("gpu", "Prints the GPU that draws", GraphicsApi.Describe);
```

`query gpu` then prints the JSON, and `query gpu --out <file>` writes it to a file. The GPU provider adds
`gpu` this way (see the console commands in the [README](../README.md)).

To add a command that does something else, call `commands.Register` with a `System.CommandLine` `Command`, as
Magic's own commands are. Reference the package in the csproj of the gem with
`PrivateAssets="all" ExcludeAssets="runtime"`, so that the gem builds against it and runs on the copy that Magic
loaded.

Give a subject or a command a name that Magic's own do not have. It joins the console before the next line is
parsed, and stays to the end of the run.

To let `set` and `query` reach the properties of a part, put `[CliPath]` on the class of the part, or on an
interface that the part provides:

```csharp
[CliPath("Window")]
public interface IWindow
```

`query Window.Title` then reads the property, and `set Window.Title "my run"` writes it. A property with no public
setter is read-only. The console shows only the properties of the type that has the attribute. Use `[CliPath]` for
state of the run, which is not saved. A value that the user keeps is a setting.

## Native libraries

A gem can call a C library. Declare the library as a `NativeLibrary` item in the csproj of the gem. The build gets the
library, builds it with CMake and generates its C# API with
[ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp). `Directory.Build.targets` documents the metadata of
the item.

- All the checkouts on a machine share the built libraries, in `Magic/NativeLibs/` under the local application data
  folder. Set `MagicNativeLibs` to move them. Set `MagicNativeCores` to limit the cores that their build uses.
- The generator reads the headers with the C headers of the machine. Call only what each platform declares.
- The API has the names of the C library. Nothing is wrapped. Add `using static` for the class of the library and the
  enums that you use.
- If more than one gem calls a library, put the library in a project of its own. The gems reference that project, and
  no `.magic` file lists it.
- Make a callback an `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` method.
- For a C API declared in C++ headers, set the `Language` metadata of the item to `c++`.
- To leave out a declaration, list it in the `Unbound` metadata of the item, with a comment that tells why.
- To run the generator by hand, run `ClangSharpPInvokeGenerator @<library>.bindings.rsp`. The build writes this file
  in the intermediate folder of the project.

## Bundled gems

The debug gems, ImGuiDebugUI and DebugTools, are in [Debugging](DEBUGGING.md). The rendering gems are in
[Rendering](RENDERING.md).

### ArchEcs

The entity world, through [Arch](https://github.com/genaray/Arch). Provides `IEcs`.

- Change the world only on the game thread. On other threads, a change throws.
- A query gives spans over the components and allocates nothing.
- A use of a dead entity, or a structural change inside a query loop, throws.

### CSharpScripting

Runs the C# scripts of the entities: the `Script` assets in an entity's `ScriptContainer` that are `.cs` files. A
`Script` asset in another language is for the gem of that language.

To write a script:

1. In the csproj of the code that holds the script, reference the project of this gem with `Private="false"`
   ([Gems that use other gems](#gems-that-use-other-gems)).
2. Write a class that implements `IBehavior`, or `ISystem` for work over many entities. Give the file the name of the
   class.
3. Give the file a `.meta` file with an id and `"type": "Script"`.
4. Give an entity the script in its `ScriptContainer`, as `"ScriptContainer": [ { "id": <the id> } ]` in a prefab.
5. To give the script values, write them beside its id: `{ "id": <the id>, "depth": 0.5 }`. Each key but `id` and
   `started` is a value, and the gem sets it on the public settable property of that name, whatever the case of the
   first letter. A value that the script has no property for, or that its type cannot hold, goes to the log and is
   left out.

How a script runs:

- Each entity gets its own copy of each script in its `ScriptContainer`. The copy starts in a later frame, when the
  world is not loading.
- The constructor gets the `Entity` for an `Entity` parameter, and a service for each other parameter. Only keep them
  in the constructor. Do the first work in `Start`.
- The values of the prefab are set on the copy before `Start`, so `Start` reads them.
- Values that change in the slot later, as the `set` command of the console writes them, are set on the copy in
  the frame after, so a script is tuned while it runs. A set marked `started`, which a `query` prints after a save,
  sets the saved fields on the copy as well, and does not start it over. A setter that throws goes to the log, and
  the handle goes from the `ScriptContainer`.
- The hooks of the behaviours run on the game thread, in the order that the copies started, after the hooks of the
  gems listed before this gem.
- A behaviour far from each camera of the player updates less often: every frame within 64 m, then one frame in n at
  n times that distance, up to one frame in 32. Its frame then gives the time since its last update as the deltas.
  `FixedUpdate` runs at each step. A behaviour on an entity tagged `global`, or on an entity without a `Transform`,
  updates each frame.
- A system runs in `Update`, each frame, wherever its own entity is. `ISystem<T1>` and `ISystem<T1, T2>` give it the
  entities that have those components, and it can change them. Give a system an entity of its own.
- A system whose work on each entity reads and writes only that entity's chunk can be an `IParallelSystem<T1>` or an
  `IParallelSystem<T1, T2>` instead: its `Run` gets one chunk, and the chunks run at the same time on the worker
  threads. Each other system stays an `ISystem`, because its `Run` gets the world and runs on the game thread alone.
- When the handle goes from the `ScriptContainer`, or the entity is destroyed, the copy ends. If the copy is
  `IDisposable`, it is disposed.
- If a script cannot start, or a hook throws, the error goes to the log, and the handle goes from the `ScriptContainer`.
- A build of the code that holds a script restarts the run.

What a save and a restart keep:

- Each save writes the state of every copy that runs into its slot: its values, by the name a prefab gives them, and
  the other fields that it keeps for itself, by name. Left out are the fields that a copy gets again when it is made:
  its entity, a thing that its constructor is handed, a service that a gem gives, a delegate, a task and a
  disposable. A member that JSON cannot hold, or whose getter throws, goes to the log, and a copy from the save starts over without it.
- A copy that a save made takes that state up and goes on from it: its slot is marked `started`, so the gem sets the
  saved properties and fields on it and calls `Resume` in place of `Start`. A spawner spawns once, and a light keeps
  the depth that it had.
- Write in `Resume` what the save does not hold and `Start` would have made: a subscription to an event, a delegate, a
  task, and the first work with a service that the copy kept. Without it, such a field is null after a restart.
- A domain that opens again while the game runs keeps the state the same way: the copies of its entities take up what
  they held and `Resume`.

### DomainImporter

Reads `.domain` files into domains. Provides `IAssetImporter<Domain>`.

- A domain is a folder with its `.domain` file and its prefabs. The file holds nothing, so `{}` is a full file.
- Each prefab in a folder named `x_y_z` is a chunk at that place. Each other prefab is at the origin of the domain.

### PrefabImporter

Reads `.prefab` files into prefabs, and writes a prefab as a `.prefab` file. Provides `IAssetImporter<Prefab>` and
`IAssetExporter<Prefab>`.

- A file is `{ "entities": [ ... ] }`. Each entity has a name, tags, components and the entities under it.
- A component is the name of its type and its values as JSON.
- An entity can name a prefab. The parts of that prefab then go under it.
- If a component has no type in the code, or its values do not fit the type, the importer logs an error. The entity
  goes without that component.
- If a prefab that a file names does not load, the file fails.
- When a prefab that a file names changes, the importer reads the file again.

### AssimpModelImporter

Reads FBX, glTF and OBJ files into models. Provides `IAssetImporter<Model>`.

- The importer puts all the nodes of a file into one model in the frame of the engine. This frame is left-handed, with
  +Y up and one metre for each unit. Triangles are clockwise when seen from outside.
- If a file has no normals or tangents, the importer makes them.
- If a mesh has no texture coordinates, the importer lays them over it from above, from its x and z, so a material
  tiles over the mesh.
- The model has one mesh for each material slot. The slots are numbered in the order that the file first uses its
  materials. The number of slots stops at the number that a renderer has.
- The `origin` in the `.meta` file is the point that the importer moves to (0, 0, 0) of the model.

### SdlBindings

The C APIs of SDL3 and SDL_image, for the gems that use SDL. `Sdl.Error` gives the reason why the last SDL call
failed. A gem that needs SDL references this project, and the csproj sets the tag of each library
([Native libraries](#native-libraries)).

### SdlPlatform

Gives the window, the clipboard and the input, through [SDL3](https://github.com/libsdl-org/SDL). Provides `IWindow`,
`IClipboard` and `IInput`. It also gets the events of the OS on the platform thread (`IPlatform`).

The settings of the gem are in `SdlPlatformSettings`. `query Window`, `query Input` and `query Simulation` list them.

#### Input

- You can connect and disconnect keyboards, mice and gamepads while Magic runs.
- Keys are physical keys (SDL scancodes). They are the same on each keyboard layout. Typed text follows the layout.
- The mouse position and motion are in window pixels. The settings scale and smooth the motion. The wheel is in
  notches.
- Gamepads use the standard layout of SDL. Sticks go from -1 to 1, with up positive. Triggers go from 0 to 1.

#### Window

- The wait for a frame rate limit shows as the `Update` of the gem in the stats.
- The title of the window is the name of the project. The icon is the default icon until you set `Icon`.
- When you close the window, the run ends.

#### Threads

- Use the members of the window and the clipboard only on the game thread. On other threads, a set throws.
- You can read the size of the window from any thread.
- A change that you make reaches the OS when the platform thread next gets the events of the OS.

#### Headless runs

Without a window, the gem needs no display. Only gamepads give input. The text of the clipboard stays in the gem.

#### Memory

The gem publishes the RAM that only this process holds, as `process.memory.usage` on its `Magic.Gems.SdlPlatform`
meter. This is the same number that the tools of the OS show:

- On Windows, the private working set.
- On Linux, the anonymous resident pages (`RssAnon`).
- On macOS, the physical footprint.

### SdlTextureImporter

Reads PNG, JPEG, TGA, BMP, SVG and ICO files into textures. Provides `IAssetImporter<Texture>`. It also writes a plain
RGBA8 texture as a PNG file, and provides `IAssetExporter<Texture>`.

- The importer keeps each texture in the cache, under the version of the file.
- If the `.meta` file asks for mips, the importer makes the mip chain.
- The importer packs each level into BC7 blocks. The usage in the `.meta` file sets sRGB for colour, or linear for a
  normal map or a mask.
- A `.meta` file with `isCompressed` off keeps plain RGBA8, for an image that the CPU reads. The image of an `.icon`
  file must have `isCompressed` off, because a window icon is plain pixels.
- The `size` in the `.meta` file of an `.svg` file sets the width that the importer draws it at.

### SlangShaderCompiler

Compiles Slang entry points to SPIR-V for the GPU provider. Provides `IShaderCompiler`.

- A shader is a `.slang` asset under `Shaders/`. `import A.B;` imports the asset at `Shaders/A/B.slang`.
- The compiler compiles an entry point with its module and all the modules that it imports. A change to one of these
  modules compiles the entry point again.
- The compiler keeps the results in the cache of the project. A second run of a shader that did not change compiles
  nothing.
- A change to the options or the tag of the compiler makes a new cache version. Then all the shaders compile again.
- The settings of the gem are in `SlangShaderCompilerSettings`. `query Debug` lists them.
- Warnings are errors. Matrices are row-major. Entry points keep their names.

> **Note:** The bindings generator cannot bind the C++ interfaces of the compiler. `Slang.cs` binds by hand the calls
> that the gem makes, by their indices in `slang.h` at the tag that the project builds.

### VulkanGraphics

The GPU through Vulkan 1.3. Provides `IGraphicsApi`.

In the `.magic` file of a project, list it after the gems that provide the window and the shader compiler. The
settings of the gem are in `VulkanGraphicsSettings`. `query Window` and `query Rendering` list them.

#### Resources

- A shader reads each resource by the id in its handle.
- The gem makes a texture, buffer, top-level acceleration structure or sampler at the first frame that asks for it by
  name. It keeps the resource while frames ask for that name with the same description. A different description makes
  the resource again.
- A buffer that a frame asks for at a different size keeps its contents, as much as fits.
- The gem destroys a resource that no frame asked for in a few frames. A gem must ask each frame for all the resources
  that it reads.
- Use the resources and frames only on the render thread. On other threads, the members throw.

#### Passes

- A frame is one `GpuCommands` value. The gems record their passes into the graph of Core, and Core gives the frame to
  this gem.
- The gem adds the barriers that each pass needs.
- A compute pass runs its shader entry point. A draw pass runs its vertex and pixel entry points. The gem compiles them
  through the shader compiler.
- A pass is not in the frame until its shaders compile. If a shader fails, the gem logs the error and the pass stays
  out.

#### GPU and ray queries

- The gem uses the fastest GPU that has Vulkan 1.3, bindless descriptors and dynamic rendering, and can show the
  window. The log names the GPU when the run starts.
- If the GPU has ray queries, `GpuFeatures` says so. Then a frame can build a bottom-level acceleration structure over
  triangles and a top-level one over instances.
- After a bottom-level structure is built, the next frame compacts it. Its name then gives a new id and address.

#### Window

- The gem draws into the window on Windows, X11, Wayland and macOS.
- Headless, there is no window image, and `WindowTarget` is none. All the other features work. Without a GPU, the gem
  uses a software device, such as Mesa's lavapipe.

#### Debug and measurements

- In a Debug build, or with the setting `Debug.IsSyncValidationOn`, the gem turns on the Khronos validation layer if
  the Vulkan loader finds it. It logs what the layer reports ([Vulkan validation](DEBUGGING.md#vulkan-validation)).
- When RenderDoc started Magic, the gem captures the frames that the `capture` command asks for
  ([RenderDoc capture](DEBUGGING.md#renderdoc-capture)). `RenderDoc.cs` binds by hand the calls that the gem makes,
  by their indices in the function table of `renderdoc_app.h`.
- The gem publishes the GPU time of each pass and each frame, and the memory, on its `Magic.Gems.VulkanGraphics` meter.
  The tags of the meter name the Vulkan version, the GPU, its kind and its driver.
- The memory in use is what the driver says that the process holds on the GPU, if the driver has
  `VK_EXT_memory_budget`. Otherwise, it is what the resources hold. The `source` tag says which.
- The allocated gauge is what the resources and the upload memory hold.

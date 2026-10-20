# Gems

A gem is a plugin: one project under this folder, built against Magic and copied beside it under `bin/Gems/<Name>/`
with everything of its own, its packages and native libraries included, which Magic loads in a context of the gem's
own when a project lists it. What a gem is to the engine, how it is listed, made and talks to the rest, is in
[Core](../../Core/README.md); each gem's README has its details. A gem named `<X>Bindings` is one other gems build
on rather than one a project lists: it holds a native library's generated API and binaries, which every gem that
references it copies beside itself, so the gems built on SDL share one SDL.

## Native libraries

A gem that calls a C library declares it to the build, in its project, and the build does the rest: it clones the
library's repository at the commit the item pins, with its submodules, builds it with CMake for this machine,
generates the library's C# API from its headers and compiles it in, and puts the library beside the gem. Nothing is
written by hand and nothing comes prebuilt or from a third party: every library is its own source, built here.

```xml
<NativeLibrary Include="SDL3" Repository="https://github.com/libsdl-org/SDL" Commit="release-3.4.18"
               Headers="include/SDL3/SDL.h" IncludePath="include"
               Cmake="-DSDL_TEST_LIBRARY=OFF -DSDL_EXAMPLES=OFF" />
```

- `Include` is the library's logical name: what the generated `DllImport`s name, what the load context resolves and
  what the build looks for among what CMake made (`SDL3.dll`, `libSDL3.so`, `libSDL3.dylib`), never a file name.
- `Repository` and `Commit` pin the source: a tag or a commit hash, fetched shallow with its submodules into
  `Build/NativeLibs/<name>/source/` (`MagicNativeLibs` moves the folder). A changed commit fetches afresh.
- `Cmake` holds the options to build with, in Release; `CmakeTarget` names one CMake target where the project builds
  more than the library, and `Source` the folder holding `CMakeLists.txt`: in the checkout when that is not the
  root, or an absolute folder of the gem's own (`$(MSBuildProjectDirectory)/Native`) for a library whose repository
  has none. Items are taken in order, so a library comes after what it builds on: `{built:<name>}` is another
  library's install folder (`-DCMAKE_PREFIX_PATH={built:SDL3}`) and `{checkout:<name>}` its checkout; `{checkout}`
  is the library's own, `{source}` the folder CMake builds from and `{folder}` the library's folder. A library is
  built once per commit and options, by one build at a time: projects that declare the same library wait on each
  other rather than build twice. An item with no `Cmake` is cloned for its headers or source alone.
- `Prepare` is a command the machine's shell runs in the checkout after cloning and before CMake, several joined by
  `&&`, with `{python}` for the machine's Python 3. It is how a C++ library gets a C API: the generator needs C, so
  a C++ library is declared through the C API its own project or ecosystem generates, run here against the pinned
  source, as Dear ImGui is through dear_bindings. No shim is written by hand, and a C API generator is never
  written where a maintained one exists.
- `Headers` are the root headers to read, relative to the checkout; `IncludePath` the folder their includes are
  found in; `Defines` what to define first. Every item's include folder serves every generation in the project, so
  a library whose headers include another's declares that one too. An item with no `Headers` is built for its
  binary alone.
- `Prefixes` names the owner of C names that have no underscore prefix (`ipl=IPL`); names with one need nothing.
  `Class` is where a name with no owner at all goes, the library's name unless it says.
- `Shims` makes a header the library's build would generate from a template, as `header=template`
  (`assimp/config.h=include/assimp/config.h.in`), with every CMake option off.

The generated API is C with Magic's names: a function `iplContextCreate` is `IPL.ContextCreate`, a struct
`IPLContextSettings` is `IPL.ContextSettings`, an enum member `IPL_SIMULATIONFLAGS_DIRECT` is
`IPL.SimulationFlags.Direct`, a handle is a typed pointer, a string is a `byte*`. Types land in the gem's own
namespace. Nothing is wrapped: the gem encodes its strings (`CString`), frees what it allocates and reads errors as
the library reports them, and the generated doc comments are the header's. A gem that hands the library callbacks of
its own (`[UnmanagedCallersOnly]` methods) finds them blittable, as runtime marshalling is off for every project
that declares a library. How the headers become C#, and what the generator refuses and why, is in the
[generator](../../../tools/NativeBindingsGenerator/README.md): it fails rather than guess, naming the header line, and
each refusal gets a rule in the generator, never a hand edit. The machine needs git, CMake and a C++ toolchain
([README](../../../README.md#requirements)).

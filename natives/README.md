# Native libraries

How every C library Magic binds is had: one CMake superbuild, `CMakeLists.txt` here, that `Directory.Build.targets`
runs for any project declaring a `NativeLibrary` item. A gem's project says what it binds, its headers and how C
names become C# ones ([Gems](../src/Bundled/Gems/README.md#native-libraries)); this folder says where each library
comes from and how it is built, for every platform, so the two never mix: a new platform, a new pin or a new option
changes a file here and no gem.

## What must hold

- **One folder per machine.** Every library lands under `MagicNativeLibs`, the user's local application data folder
  (`Magic/NativeLibs/` under it) unless the variable says otherwise, shared by every checkout and worktree, since a
  library built once serves them all. Under it each library has `source/` (what was fetched), `build/` (CMake's
  working folder), `built/` (what it installed) and `built/native/<file>`, the one shared library the build copies
  beside each gem that binds it, followed through the links Linux and macOS install it as. A library built from a
  CMake folder of this repository's own is copied to `project/` first, since CMake ties a build folder to one source
  path and that path must not be a checkout's. The superbuild's own working folder is the checkout's `Build/natives/`.
- **Pinned, then fetched or downloaded.** A library is fetched from its repository at a tag, shallow and with its
  submodules, or downloaded as the release archive its author publishes, whose SHA-256 is pinned beside the URL. A
  published build serves where one exists for the platform, the source build everywhere else, as the shader compiler
  shows: Microsoft's build on Windows and Linux x64, built here on macOS. Nothing else is prebuilt or comes from a
  third party. A changed tag fetches afresh; the update step never touches the network otherwise.
- **Built once per machine.** CMake's ExternalProject stamps record each step done, so a build cut short resumes at
  the step that did not finish. Over them, a hash of this folder's files names a `ready.<hash>` marker in the shared
  folder: a change to any file here runs the superbuild again, which rebuilds only what the change reaches, and the
  marker is written when the whole succeeded. One build at a time, machine-wide, under a named mutex, since the
  folder is shared.
- **Bounded.** The superbuild runs one library at a time, and each library's own build on half the machine's cores
  unless `MagicNativeCores` names how many, below normal priority, so a long build never makes the machine unusable
  and the memory it takes, a few hundred MB per compiler, is bounded with the cores. Ninja is the generator
  everywhere: on Windows CMake runs inside Visual Studio's developer environment, found through vswhere, which brings
  its compiler, CMake and Ninja; elsewhere CMake and Ninja come from the PATH ([requirements](../README.md#requirements)).
- **Named plainly.** Each library's shared library is `<name>.dll`, `lib<name>.so` or `lib<name>.dylib`, found
  under `built/` then `build/`, or at the path named where an archive holds several; a library whose build would name
  it otherwise is told not to (Assimp's compiler suffix). The name is what the gem's `DllImport`s name and what the
  gem load context resolves.
- **Options are the library's, platform differences are here.** Each library builds in Release with the options its
  block names, and a block says why an option is what it is. Where a platform needs something else, the condition
  sits in that library's block, as Assimp's zlib does on macOS.

A generated C API needs a generator's source too: Dear ImGui's comes from dear_bindings, run here with Python 3 and
the `ply` package against ImGui's headers, then built with ImGui's sources by `dcimgui/CMakeLists.txt`. The library's
defines there are the ones the gem's project reads the headers with, so what is compiled is what is bound.

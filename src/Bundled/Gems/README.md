# Gems

A gem is a plugin: one project under this folder, built against Magic into `Build/bin/<Configuration>/Gems/<Name>/`
with everything of its own, its packages and native libraries included, which Magic loads from `Gems/<Name>/<Name>.dll`
beside it, in a load context of the gem's own, when a project lists it. What a gem is to the engine, how it is listed,
made and talks to the rest, is in [Core](../../Core/README.md#gems). Each gem's README has what Core's does not: the
gem's own choices, its limits and what must hold for it.

## Native libraries

A gem that calls a C library declares it to the build, in its project, and the build does the rest: it clones the
library's repository at the commit the item pins, with its submodules, builds it with CMake for this machine,
generates the library's C# API from its headers and compiles it in, and puts the library beside the gem. Nothing comes
prebuilt or from a third party: every library is its own source, built here. No binding is written by hand, except
where the generator cannot express the API, a COM interface; the gem whose library needs that says so in its own
README.

### Declaring a library

A library is one `NativeLibrary` item in the gem's project. Its `Include` is the library's logical name: what the
generated `DllImport`s name, what the load context resolves and what the build looks for among what CMake made
(`<name>.dll`, `lib<name>.so`, `lib<name>.dylib`), never a file name. `Repository` and `Commit` pin the source, a tag
or a commit hash, fetched shallow with its submodules into `Build/NativeLibs/<name>/source/` (`MagicNativeLibs` moves
the folder); a changed commit fetches afresh. Both are required.

How it is built: `Cmake` holds the options, built in Release; `CmakeTarget` names one CMake target where the project
builds more than the library, and `Source` the folder holding `CMakeLists.txt`, in the checkout when that is not the
root, or an absolute folder of the gem's own (`$(MSBuildProjectDirectory)/Native`) for a library whose repository has
none. `Prepare` is a command the machine's shell runs in the checkout after cloning and before CMake, several joined
by `&&`, with `{python}` for the machine's Python 3: it is how a C++ library gets a C API, since the generator reads
C, so a C++ library is declared through the C API its own project or ecosystem generates, run here against the
pinned source. No shim is written by hand, and a C API generator is never written where a maintained one exists.
Items are taken in order, so a library comes after what it builds on: `{built:<name>}` is another library's install
folder (`-DCMAKE_PREFIX_PATH={built:<name>}`) and `{checkout:<name>}` its checkout; `{checkout}` is the library's
own, `{source}` the folder CMake builds from and `{folder}` the library's folder. A library is built once per commit
and options, by one build at a time: projects that declare the same library wait on each other rather than build
twice. An item with no `Cmake` is cloned for its headers or source alone.

How its API is generated: `Headers` are the root headers to read, relative to the checkout; `IncludePath` the folder
their includes are found in; `Defines` what to define first. Every item's include folder serves every generation in
the project, so a library whose headers include another's declares that one too. An item with no `Headers` is built
for its binary alone. `Prefixes` names the owner of C names that have no underscore prefix (`abc=Abc`); names with
one need nothing. `Class` is where a name with no owner at all goes, the library's name unless it says. `Shims`
makes a header the library's build would generate from a template, as `header=template`, with every CMake option
off.

The generated API is C with Magic's names, in the gem's own namespace: nothing is wrapped, so the gem encodes its
strings, frees what it allocates and reads errors as the library reports them, and the generated doc
comments are the header's. A gem that hands the library callbacks of its own (`[UnmanagedCallersOnly]` methods) finds
them blittable, as runtime marshalling is off for every project that declares a library. How C names become C# ones,
how the headers are read, and what the generator refuses and why, is in the
[generator](../../../tools/NativeBindingsGenerator/README.md): it fails rather than guess, naming the header line,
and each refusal gets a rule in the generator, never a hand edit. The machine needs git, CMake and a C++ toolchain
([README](../../../README.md#requirements)).

### Sharing a library

A native library loads once in the process, from the folder of the first gem that binds it, and every gem that binds
the same name after that gets the one copy, through the gem's load context: two copies would each keep state of
their own, so a window one copy opened would mean nothing to the other. So one project declares each library, and
the gems that bind it reference that project, which copies the API and the binary beside each of them. Where several
gems share a library that none of them owns, a bindings project holds it: a gem named `<X>Bindings` is one other gems
build on rather than one a project lists, with no `IGem` of its own, holding only a library's generated API and
binaries. The gems built on the platform's library share it this way, and the first of them to bind it loads the copy
they all use:
the platform gem, as a GPU provider is made after it.

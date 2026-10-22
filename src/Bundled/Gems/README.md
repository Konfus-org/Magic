# Gems

A gem is a plugin: one project under this folder, built against Magic into `Build/bin/<Configuration>/Gems/<Name>/`
with everything of its own, its packages and native libraries included, which Magic loads from `Gems/<Name>/<Name>.dll`
beside it, in a load context of the gem's own, when a project lists it. What a gem is to the engine, how it is listed,
made and talks to the rest, is in [Core](../../Core/README.md#gems). Each gem's README has what Core's does not: the
gem's own choices, its limits and what must hold for it.

## Native libraries

A gem that calls a C library declares it in its project, and the build does the rest: it has the library fetched
and built for this machine, generates the library's C# API from its headers and compiles it in, and puts the library
beside the gem. Every library is pinned, and comes from its own source, built here, or from what its author
publishes for the platform; nothing comes from a third party. No binding is written by hand, except where the
generator cannot express the API, a COM interface; the gem whose library needs that says so in its own README.

### Declaring a library

A library is one `NativeLibrary` item in the gem's project, and the item says all of it, so a gem carries its
libraries with it when it is added or removed. Its `Include` is the library's logical name: what the generated
`DllImport`s name, what the load context resolves, and the name of the library's folder. The item says where the
library comes from, a repository at a tag or the archive its author publishes with the archive's SHA-256; how CMake
builds it, the options it is given and the one target to build where it is not installed; the libraries it is built
after and against; and what is bound of it. The metadata are declared, with what each means, beside the build that
reads them, in `Directory.Build.targets`. Where a platform needs something else, the gem's project says so with a
condition, as the shader compiler's does, which comes prebuilt where its author publishes a build and from source
elsewhere. An item with no `Headers` is bound by hand and declared for its binary alone; one declared `HeadersOnly`
is fetched for its headers and sources alone, as another library's input is.

A library with no build of its own, written to be compiled into its user, is declared by its sources: the build
writes the CMake project that compiles them into one shared library, with the defines its headers are read with, so
what is compiled is what is bound, and the include folders it and the libraries it depends on declare. On Windows its
functions are exported as Linux and macOS export them, without a declaration of their own. No CMake file is kept in
the repository: the superbuild, in the project's intermediate folder, and the project of a library declared by its
sources, beside it, are what the build writes from the items, to read when a library misbehaves and never to edit.

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
and each refusal gets a rule in the generator, never a hand edit.

### Building a library

Each library is had through CMake's ExternalProject: the build writes the project's items as one superbuild, a call
each, and builds the libraries that are not ready, one at a time, in the order the project declares them, so a
library is declared after those it depends on. A gem's libraries are built as its build starts, before the projects
it references, so a cold native build goes on while Core compiles, and other gems' libraries build beside them. What
must hold:

- **One folder per machine.** Every library lands under `MagicNativeLibs`, the user's local application data folder
  (`Magic/NativeLibs/` under it) unless the variable says otherwise, shared by every checkout and worktree, since a
  library built once serves them all. Under it each library has `source/` (what was fetched), `build/` (CMake's
  working folder), `built/` (what it installed) and `built/native/<file>`, the one shared library the build copies
  beside each gem that binds it, read through the links Linux and macOS install it as. A library declared by its
  sources has `project/`, the CMake project written for it, there too, since CMake ties a build folder to one source
  path and that path must not be a checkout's. A gem's project may name these folders in an option, as a library
  built against another names that one's `built/`.
- **Pinned, then fetched or downloaded.** A library is fetched shallow from its repository at a tag, with its
  submodules, or downloaded as the archive its author publishes, verified against the SHA-256 the item pins. A changed
  tag fetches afresh; nothing else touches the network.
- **Built once per machine.** A hash of what CMake is told of a library, and of the libraries it depends on, names a
  ready marker in its folder: a library is built again when its item changes, or one it depends on does, and the
  marker is written when it built. When every library a project declares is ready, nothing runs, not even CMake, and
  a build that finds nothing changed since the last, neither the project nor its items nor a library's binary in the
  shared folder, which another checkout's build of it rewrites, starts no native work at all. CMake's ExternalProject
  stamps record each step done, so a build cut short resumes at the step that did not finish.
- **One build of a library at a time.** The folder is shared, so each library has a named mutex, machine-wide. A
  project holds those of every library it declares, as its superbuild writes in all their folders, taken in name
  order so two builds never wait on each other; whoever comes second finds the markers.
- **Bounded.** Two libraries build at once on the machine, each in one of two lanes, and each library's own build runs
  on half the machine's cores unless `MagicNativeCores` names how many, below normal priority: a second library fills
  what the first leaves idle, as configuring, linking and fetching are one core's work, while the two together take
  the machine's cores at the default, so a long build never makes the machine unusable and the memory it takes, a
  few hundred MB per compiler, is bounded with the cores. Ninja is the generator everywhere: on Windows CMake runs
  inside Visual Studio's developer environment, found through vswhere, which brings its compiler, CMake and Ninja;
  elsewhere CMake and Ninja come from the PATH ([requirements](../../../README.md#requirements)).
- **Named plainly.** Each library's shared library is `<name>.dll`, `lib<name>.so` or `lib<name>.dylib`, found under
  `built/` then `build/`, or where the item names it in an archive that holds several; a library whose build would
  name it otherwise is told not to (the model importer's compiler suffix). The name is what the gem's `DllImport`s
  name and what the gem load context resolves.
- **Said as it happens.** The build logs a line as each library starts, with where it comes from, and as it is
  ready, and what CMake, git and the compilers print one verbosity down (normal), as they print it. A logger that
  shows a build's messages shows them live: an IDE's build output, CI's log, `dotnet build` with its output
  redirected or with `-tl:off`. The terminal logger `dotnet build` uses in a terminal shows only the target running
  and its time while a task runs, and a task's messages at detailed verbosity alone, after its project ends.
- **A real build's work alone.** An IDE builds each project in the background to feed its editor, a design-time build
  that hides what it logs and skips the compiler. Such a build compiles the API the last real build generated, and
  neither builds a library nor generates one, as a native build in it would run unseen. So the editor knows a
  library's API from the first real build on.
- **The generator is a reference.** A gem that declares a library references the bindings generator for the order it
  builds in alone, so the generator is restored with everything else and built once per build, only where a library
  is declared. Generation reruns when the generator, the item or the library's binary changed, but rewrites only a
  file whose text changed, so a gem compiles again only when its API did.
- **Long steps yield.** A native build and a generation give their MSBuild node back while they run, since a project
  waiting on a reference resumes only on the node it started on: one gem's long step never holds up gems that have
  nothing to do with it.

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

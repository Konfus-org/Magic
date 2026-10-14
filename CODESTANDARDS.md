# Toybox CodeStandard

## Core Engineering Policies

- **Scope**: Keep changes isolated and highly reusable.
- **Duplication**: Avoid redundant code patterns without building single-use helper functions.
- **Simplicity**: Prioritize the simplest, most direct solution first. Avoid over-engineering, unnecessary abstractions, or predicting future edge cases. Add complexity only when a specific problem requires it.
- **Housekeeping**: Permanently delete stale definitions instead of leaving commented placeholders.
- **Small changes**: Make the smallest change that does the job, and keep it simple.
- **Modern C#**: Use current C# and .NET, and follow the `.editorconfig` (the build enforces most of it).
- **Portable**: All code runs on every major platform; see [Cross Platform](#cross-platform).
- **Dependencies**: Third-party libraries come from NuGet.

## Code style

These rules apply to all C# in the repository. New code must follow them.

### Type layout

Members go in this order, with one blank line between members and between groups:

1. Constants and static fields
2. Instance fields
3. Constructors and finalizer
4. `Dispose`
5. Events, then properties
6. Public and internal methods
7. Private methods
8. Nested types

`Dispose` sits directly under the last constructor, so setup and teardown read together. A type with no
constructor of its own (a primary-constructor type included) has it as its first method, above all the others.

An interface's members are public members, placed with the other public ones of their kind: properties with
properties, methods with methods. `IX.Member` is only for a member that can't be public because it would share a name
with another one (the untyped `IEnumerator.Current` beside a typed `Current`), and it goes directly under the public
member it stands in for.

A primary constructor when the constructor only keeps its parameters; an explicit one when it does work (builds a
query, makes a delegate, subscribes).

Within a group, public comes before internal, and internal before private. Keep related members next to
each other.

### Visibility

Public is for what other parts use; everything else is `internal` (or private), wherever it lives. A Core type
is internal unless a gem or a game script needs it: the container and other host plumbing never are. A type only
one gem uses lives in that gem, internal; Core holds the types gems share. Tests see only what is public (no `InternalsVisibleTo`):
nothing is public for their sake, a member only its own type uses is private, and what is not public is not tested
(see [tests/README.md](tests/README.md)).

### Folders

A namespace is its project's root namespace (`Magic` for Core, `Magic.<Name>` for a gem or a project's code) and then
the folders below the project, and the build checks that every file's matches.

Core files its types by what they are, one folder and namespace each, so where a new file goes is never a judgement
call. A type goes in the first of these that fits:

1. **The root**: what starts the run.
2. **`Internal`**: what is not public.
3. **`Interfaces`**: interfaces.
4. **`Attributes`**: attributes.
5. **`Extensions`**: `XExtensions` classes.
6. **`Contexts`**: the state of the run and the frame, as Core hands it to gems, the subscriptions that say how long
   what is added to them lasts, and every other enum, record and struct: the plain data gems pass each other.
7. **`Services`**: services and nothing else: what is registered for gems to ask for, and static services such as
   the log.

A folder exists only once something is in it, and holds only what its rule says: a type that fits none of them is a
reason to agree a new folder, not to stretch one. A folder grown too big to take in at a glance is split by area, a
subfolder and namespace each, never named like a type in it (`Frames` holds `Frame`); what is no one area's stays at
its top.

A gem is flat: every file at its root, in the gem's one namespace (`Magic.<Gem>`), since a gem stays small enough to
take in at a glance. Its `Resources/` folder holds assets, scripts among them, never the gem's own code; a gem that
ships with Magic has none, as its assets are in `src/Bundled/Resources/`.

### Files

A file is named for its main type. Small types that belong with it and go in the same folder can share its file (an
enum one of its properties takes, the record that goes with it); a type that stands on its own has a file of its own.

### State

- Public and internal state is exposed as `PascalCase` properties, never as fields.
- If a value is set once, use `{ get; }` and assign it in the constructor.
- If a value can change, use `{ get; set; }`, or `{ get; private set; }` when writes need a tighter scope.
- If a value is derived from other state, use an expression-bodied property (`=>`).
- Private state is a private field (`_camelCase`), declared with the other fields at the top of the type, whatever it
  holds: an injected dependency, a lock, a collection, a delegate made once and kept (`_renderFrame = RenderFrame`,
  so a frame allocates no closure), a flag, this frame's values for a callback. `{ get; set; }` is for what other code reads;
  where reading or writing private state has to do something, that is a method.
- The exception is interop and data structs, where layout or the JSON shape depends on fields
  (`[StructLayout]`, GPU structs, ECS components). They keep their public fields.

### Null

- The null-forgiving `!` (`value!`, `null!`, `default!`) is not allowed; the build fails on it (`NX0001`-`NX0003`,
  from Nullable.Extended.Analyzer), and on every nullable warning. No `// !` comment to excuse one either.
- What is always there is not declared nullable: pass it in, make it `required`, or group what comes and goes
  together into one optional object.
- What may be missing is handled where it is read: a guard clause, `is not { } value`, `??` with a fallback or a
  `throw` that says what was expected. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a
  check proves (`Result<T>.Ok`).

### Async

- Every `await` says `.ConfigureAwait(false)`; the build fails without it (`CA2007`). Tests are the exception:
  xUnit runs them on its own context.
- A task is awaited, kept to be awaited later, or let go with `.FireAndForget()`, which logs what it throws. Never
  dropped (`VSTHRD110`, `CS4014` fail the build), never discarded with `_ =`: that hides what it threw and says
  nothing about what was meant.
- Ordinary async and background work is tasks: async methods, `Task.Run`, `.FireAndForget()`. `Threads` is for the
  engine's own threads, and for work that lasts as long as the run.
- Work handed to another thread with `Threads.Post` returns nothing, so there is no task to forget; `InvokeAsync` when
  the caller needs it done or what it returns. Nothing blocks waiting on the main or render thread.
- Task-returning methods end in `Async` (`VSTHRD200`); no `async void` (`VSTHRD100`).

### Performance

Asynchronous where work can wait, and fast in the frame loop:

- **No LINQ in the frame loop.** Nothing that runs every frame (`Update`, `Render`, physics, a system's `Run`, a
  render pass) uses LINQ, allocates closures or formats strings: plain loops, delegates made once and kept.
- **Spans for chunks of data.** Pass `Span<T>`/`ReadOnlySpan<T>` instead of slicing arrays onto the heap. A type that
  must stay on the stack is a `ref struct`.
- **`ValueTask` when it is usually done.** A method that completes synchronously most of the time (a cache check)
  returns `ValueTask`/`ValueTask<T>`, not `Task`.
- **Off the main thread where it can be.** Work over many entities is a parallel system; loading and building are
  tasks that hand what they made to the main thread in one batch; drawing is the render thread's.
- **Channels for signalling.** Thread-safe hand-offs between threads and event streams use
  `System.Threading.Channels` rather than locks around a queue.
- **One JSON dialect.** JSON is read and written in [Core's](src/Core/README.md#json) dialect; no other options
  object is made.
- **Function pointers for hot native calls.** Engine hooks and native libraries called often are wrapped with
  `delegate*` rather than delegates.

### Disposables

What hands out something that ends when disposed (an event or file watch, an ECS observer, a scheduled system, a
registration, an attachment) returns `Subscription`, not `IDisposable`, and whoever keeps it holds a `Subscription`,
so reading the code says what the thing is and why it is kept. Several kept for as long as their owner go in a
`SubscriptionList`, which one `Dispose` ends, rather than a loop over them.

### Gems, systems and scripts

- A gem is `internal sealed class X : IGem` plus the Core interfaces it provides; what it depends on, its own
  settings object included, are constructor parameters, including what it uses of another gem's work (a renderer
  takes the window it draws into): that order is the order gems are made in and, reversed, disposed in.
- A gem never reads the command line or the project file: what it can be told arrives as its settings object, plain
  data like a file type (Serialization). Its settings can change while running, between frames, so a setting it reads
  only as it starts is `[Browsable(false)]`, and a settings window does not offer what would change nothing.
- A gem frees what it started (a native library, a subsystem) in `Dispose`, once nothing of its own is still running
  on a worker.
- A gem's constructor, `Dispose` and `Render` run on the render thread; `Update`, `FixedUpdate` and `LateUpdate` on
  the main thread. What a gem owns is used on one of them, and the other hands it work through `Threads`. `Render`
  reads only what the gem copied in `LateUpdate`, since the main thread is changing everything else.
- A component is an unmanaged struct with public fields that implements `IComponent`; declaring it is all it takes for
  the ECS to hold it and a file to name it.
- Work over entities is a system that names the components it looks at, up to four (`ISystem<T1>` and on; `ISystem`
  for every entity), and an `IParallelSystem` when it is work over many entities. Its `Phase` is stated when not
  `Update`: `Update`, `FixedUpdate` or `LateUpdate`. It is added to the world (`World.Add`) and kept as a
  `Subscription`. What is not about entities (pumping events, presenting, loading) is an `IGem` hook.
- What a gem tells others has happened is a message, a struct, on `Events`; whoever listens watches it and keeps the
  `Subscription`.
- A script is a `.cs` asset under a project's `Assets/`, a gem's `Resources/` or `src/Bundled/Resources/`, its class
  named like the file and an `IScript`: an `IBehavior` for what one entity does each frame, and a system when it works
  over many. The project's code, or the gem it belongs to, compiles it, and a prefab attaches it to an entity by the
  file's id. A game is its assets and that code, never a gem of its own. Scripts work as components do: a prefab holds
  them with their values set, and each entity spawned gets its own copy. So its constructor takes only what it asks for
  as a gem's does, never its `Entity`; its values are its public `{ get; set; }` properties; and `Start(entity)` is
  where it begins on its entity.

### Serialization

- A type read from a file is plain data: properties, and no JSON attributes, I/O or `Open`. Turning bytes into it
  and finding its file are separate code beside it, and nothing else repeats its shape.
- Its properties follow the file rather than State's constructor rule: a key the file must have is a `required`
  property with `{ get; init; }`, and one with a default is `{ get; set; }` with the default as its initialiser.
- What code sets rather than the file has no public setter, so it is never read from a file.
- Nothing is declared for a type to be read or written: Core's one dialect reaches any type by reflection.

### Assets

- An asset type is a sealed class implementing `IAsset<T>` of itself: what its users read, never how its file was
  read. It states its file extensions as a static, so no code names them, and has a `Handle { get; init; }`, the one
  thing on it Core's `Assets` sets rather than its file. A type gems share is Core's; one only its gem reads is that gem's.
- A type whose file is itself as JSON implements `IJsonAsset<T>`, and one whose file is its text `ITextAsset<T>`:
  Core reads both. Any other format is an `IAssetLoader<T>` a gem provides by implementing it, returning `Result`, and
  what the `.meta` sidecar holds beside the id is its import settings.
- Components, assets and the project name an asset by its typed handle, never by a path: a path is for code that
  starts from a name.

### Defaults

- A setting's default is its property initialiser; a file's, its type's initialisers; a script's, its properties'
  initialisers; a component's, its getters (`Camera`). Nothing else states it again: no static `Default` instances or
  constants beside them.
- Every asset type has a default asset, `<Type>s/Default.<ext>` (`<Type>s/Default/Default.<ext>` for a folder
  asset, a domain). An asset setting is a path, empty for that default.
- A value an implementation falls back to on its own (the renderer's background with no sky) is defined once in it,
  where it is easy to find and change, never on a Core type.
- The few "nothing" values are `.None` (`GpuTexture.None`, `GpuBuffer.None`).

### Failures

- A `Result` for what can fail on content or the machine: I/O, import, compile, readback.
- Broken content is `Debugging.Error` and a visible stand-in (the glowing failure surface, the failure cube), never
  a stopped frame.
- What works, only worse, is `Debugging.Warn`.
- An exception is for a programmer's mistake, and for a gem that cannot exist at all (no GPU device).
- `Debugging.Assert` is for state that cannot be, where going on would be worse than stopping: it ends the process at
  once, leaving a crash file. A caller's mistake at an API stays an exception, which takes only that gem out of the
  loop.

### Methods

A method should read from top to bottom:

1. Guard clauses and validation first, returning or throwing early.
2. Setup next, using `using` declarations rather than nested `using` blocks.
3. The main path, kept at one level of indentation where possible. Prefer early `continue` and `return` to
   nested `if`/`else`.
4. The result.
5. Local functions last, after the final `return`.

Separate these steps with a blank line. Long methods are fine as long as they read in order.

### Naming

- Read a name as `Class.Member(arguments)`. If that says what happens or what is held, the name is good,
  however short: `Pipelines.Invalidate(ctx, shaders)`, `Shaders.Cached(ctx, id)`. If it does not
  (`StreamingSystem.Pump()`, `_scratch`), name the object: `SpawnReady()`, `_unwanted`.
- A reusable buffer is named for its one purpose, and has only one.
- No single-letter locals or lambda parameters, except loop counters, `x`/`y`/`z`, and the coefficients of a
  published formula. Established short words (`ctx`, `cls`, `sb`, `dt`, `ex`) stay.
- A local does not change meaning within a method, and does not shadow a type.
- Every method that returns a `Task` or `ValueTask` ends in `Async`, private helpers included
  (`LoadAsync`, `GenerateAsync`, `LoadFileAsync`). A method that waits for the same work on the calling thread has
  the name without it (`IFileSystem.Read` beside `ReadAsync`). Test methods are the exception: their name is the
  sentence they check.
- An async method takes a `CancellationToken cancel` as its last parameter and passes it to everything it awaits,
  unless nothing in it could stop. Long work whose share done can be measured (opening a domain, LOD
  generation) also takes an `IProgress<float>? progress`, 0 to 1, just before the token; a
  single step (one file read, a hand-off to a thread, a compile) does not. Both are optional (`= null`,
  `= default`) on a service's public methods and required on interfaces and private helpers.

### Extensions

Extension members use `extension(T x) { }` blocks in an `XExtensions` class, filed as [Folders](#folders) says.
Related types (a family of enums converted the same way) share one class, one block each.

### Comments

- XML doc comments on types and non-obvious members. Say why, not what.
- `<summary>` tags sit on their own lines, even for a one-line summary:

  ```csharp
  /// <summary>
  /// Where Assets live.
  /// </summary>
  ```

  Not `/// <summary>Where Assets live.</summary>`.
- No divider comments (`// ---- Section ----`, `// ====`) and no `#region`.

# Cross Platform

Magic targets Windows, Linux and macOS from one code base. Nothing below is optional on the grounds that "it
works on my machine": the other machines are the point.

## Paths

- Never write a separator, split on one, or compare path prefixes as strings. Build a path with
  `IFileSystem.Combine`, and take one apart with `IFileSystem.Parent`. `FileSystem` is the one place that knows what a
  separator is.
- `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine: they know every OS's rules.
- No drive letters, no absolute paths, no checking a string for `..`.
- **An asset path is not an OS path.** It is relative to its asset root and always uses `/`, on every OS.
  Core's `Assets` converts at its boundary; nothing else converts one into the other.

## Disk

- All disk access goes through `IFileSystem`, never `File` or `Directory`.
- The one exception is `Debugging`, the log: it writes crash files itself, so that it works before any service
  exists and after one has broken.
- Engine output goes beside Magic, a folder per kind (`Logs`, `Cache`, `Screenshots`), never a hardcoded temp, home
  or AppData path.
- File watch events differ per OS: duplicated, coalesced, out of order. Treat one as "something changed here",
  collect them in one place, and check what exists.

## Case

Linux file systems are case-sensitive. A file name in code, an asset, a shader `#include`, a csproj or a `.meta`
matches the file on disk exactly. The engine compares paths ignoring case so that every OS behaves the same,
which means two files may never differ by case alone.

## Text

- Files and generated shader source are written with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM.
  Readers accept `\r\n` and a BOM.
- Numbers and dates are parsed and formatted culture-invariantly.

## Platform code

- No OS APIs in Core: no P/Invoke, no registry, no `.exe`/`.so`/`.dylib` names, no `OperatingSystem.Is*`.
  Anything platform-specific lives in a gem, behind one of Core's interfaces (`IWindow`, `IInput`, `IGpu`),
  guarded by `OperatingSystem.IsWindows()` and its siblings.
- A native library is referenced by its logical name (`"name"`, never `"name.dll"`). Its binaries come from NuGet,
  and its gem's load context picks the one for the machine it runs on.
- A native library loads once. Each gem loads in its own context, but a library one gem has loaded is the one every
  gem that binds the same name gets (`GemLoadContext`), so a window it opened is one window to every gem. The
  versions agree because `Directory.Packages.props` pins one per package.
- No csproj names a runtime identifier: a gem carries every platform's native binaries, and its load context picks
  the machine's.

## Graphics

- Drawing goes through `IGpu`: a gem makes GPU objects and records a frame's commands as plain data
  (`GpuCommands`), which the GPU gem turns into its API's calls. Backend-specific code lives only in that gem.
- What is drawn into is a render target, a `GpuTexture`: a render texture, or a window's (`IGpu.Target`). Drawing code
  takes a target and asks its format and size; it never assumes a window.
- A GPU feature is used only if Vulkan, D3D12 and Metal all have it.

### HLSL

- Shaders are HLSL only, compiled by whichever gem provides `IShaderCompiler`, which loads their `#include`s from
  the assets: HLSL 2021, Shader Model 6.0 with wave intrinsics, warnings are errors.
- Resources bind through `Include/Bindings.hlsli`'s macros (`READ`, `SAMPLER`, `WRITE`, `UNIFORM`), never a register
  by hand. Its header holds the conventions every shader is written against: frame, winding, reverse-Z, matrices,
  physical light units and pre-exposure, colour.
- Wave intrinsics only where the result cannot depend on the wave's size, 32 or 64 by GPU. No 16-bit types and no
  runtime-indexed resource arrays.
- A value that can go wrong is kept from it where it arises: roughness clamped (`RoughnessToAlpha`), n·v kept above 0,
  `NormalizeOrZero` where a vector can be zero, explicit gradients or levels (`SampleGrad`, `SampleLevel`) inside a
  branch.
- Few variants: a define for what changes the pipeline (which surface, forward or not), a `[branch]` on a value the
  whole draw shares for what does not.
- One copy of each piece: the BRDF, the lights and their units, and the shading models live once, in the shared
  shader library, and every renderer shades with them.
- A technique from a paper is written here from the paper, and its comment names the paper; no third-party shader
  code is copied in.

## Scripts and tools

Prefer one cross-platform script over a script per OS: repo scripts are written in a cross-platform scripting
language such as PowerShell (`pwsh`), or are a `dotnet` command. No `.bat`/`.sh` pairs.

## Tests

Tests pass on every supported OS. They touch no disk; a path in an input or an expectation is built with
`Combine`, never written Windows-shaped (`C:\`, `\\`).



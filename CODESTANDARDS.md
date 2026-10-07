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
- **Gems**: Core is the runner and holds every type more than one gem names; every feature is a gem. A gem is an
  implementation that references Core only (`Private="false" ExcludeAssets="runtime"`), so swapping one is a line in
  the project's `gems` list. The exception is a gem that reads another gem's public markers: it references that gem
  (its csproj, the same way), which then loads first. The one case today is EntityHierarchy, whose `Static`,
  `Hidden` and `Settled` DeferredRenderer and ChunkStreaming query. No soft dependencies: nothing found by name, no
  component one gem writes and another polls without a reference between them, no path into another gem's assets.

## Layout

Every part (Core, each gem) is one folder laid out the same way:

| Where | What |
| --- | --- |
| `<Part>.csproj` | Named for its folder (`Magic/Core/Core.csproj`, `Magic/Gems/World/ChunkStreaming/ChunkStreaming.csproj`). |
| `<Part>.cs` | The gem's `IGem` class, at the root (Core's `Program`). |
| `Contexts/` | Public data: components, assets, events, records, enums, attributes, handles, and settings another part reads. |
| `Services/` | Public interfaces, and public classes that do the work (extension classes included). |
| `Internal/` | Everything that is not public, settings only the part itself reads among it; subfolders by concern are fine (`Internal/Pipeline`, `Internal/Json`). |
| `Resources/` | The part's own assets, added to the assets while it is loaded (Core's for the whole run). |
| `Tests/` | Its unit tests, `<Part>.Tests.csproj` ([TESTING.md](TESTING.md)). |

`Resources/` is rigid: it holds only assets the part provides, each in the folder named for its kind (`Domains/`,
`Fonts/`, `Icons/`, `Materials/`, `Models/`, `Passes/`, `Pipelines/`, `Presets/`, `Scripts/`, `Shaders/`,
`Textures/`) beside its `.meta`, and READMEs. Core's holds what is generic, what more than one part or every project
uses; a gem's, what only it reads. A part reads assets through `Assets`, by id or by a path under a root, never from
disk by a path of its own. Shipped, every part's `Resources/` are one folder, so no two parts have a file at the same
path. A project's `Assets/` is laid out as it likes.

Core's `Contexts`, `Services` and `Tests` have a folder per area (`Assets`, `Debug`, `Ecs`, `Maths`, `Platform`,
`Randomness`, `Rendering`, `Scripting`, `World`); what is the runner's own, or every area's, sits at their root
(`Frame`, `Threads`, `AssetJson`).

A gem that provides several services may split its class into partials in `Internal/`, one file a service
(`SdlPlatform.Windows.cs`, `SdlPlatform.Input.cs`).

Namespaces do not follow the folders: Core's types are in the shared namespaces gems use
(`Magic.Contexts.Components`, `Magic.Interfaces`, `Magic.Services`), and a gem's internals are in its own
(`ChunkStreamingGem`). The solution's folders mirror the disk.

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
8. Other interface implementations
9. Nested types

`Dispose` sits directly under the last constructor, so setup and teardown read together. A type with no
constructor of its own (a primary-constructor type included) has it as its first method, above all the others.

A primary constructor when the constructor only keeps its parameters; an explicit one when it does work (builds a
query, makes a delegate, subscribes).

Within a group, public comes before internal, and internal before private. Keep related members next to
each other.

### Visibility

Public is for what other parts use; everything else is `internal` (or private), wherever it lives. A Core type
is internal unless a gem or a game script needs it: the container and other host plumbing never are. A type only
one gem uses lives in that gem, internal; Core holds the types gems share. Tests see only what is public (no `InternalsVisibleTo`):
nothing is public for their sake, a member only its own type uses is private, and what is not public is not tested
(see [TESTING.md](TESTING.md)).

### State

- Public and internal state is exposed as `PascalCase` properties, never as fields.
- If a value is set once, use `{ get; }` and assign it in the constructor.
- If a value can change, use `{ get; set; }`, or `{ get; private set; }` when writes need a tighter scope.
- If a value is derived from other state, use an expression-bodied property (`=>`).
- Private fields are only for injected dependencies, locks, internal collections, delegates made once and kept
  (`_turn = Turn`, so a frame allocates no closure), and backing fields that do real work such as validation.
  What else a class keeps between calls (a flag, this frame's values for a callback) is a private property.
- The exception is interop and data structs, where layout or the JSON shape depends on fields
  (`[StructLayout]`, GPU structs, ECS components). They keep their public fields.

### Null

- The null-forgiving `!` (`value!`, `null!`, `default!`) is not allowed; the build fails on it (`NX0001`-`NX0003`,
  from Nullable.Extended.Analyzer), and on every nullable warning. No `// !` comment to excuse one either.
- What is always there is not declared nullable: pass it in, make it `required`, or group what comes and goes
  together into one optional object (`ViewBuffers.Occlusion`).
- What may be missing is handled where it is read: a guard clause, `is not { } value`, `??` with a fallback or a
  `throw` that says what was expected. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a
  check proves (`Result<T>.Ok`, `RefCountTable.TryAcquire`).

### Async

- Every `await` says `.ConfigureAwait(false)`; the build fails without it (`CA2007`). Tests are the exception:
  xUnit runs them on its own context.
- A task is awaited, kept to be awaited later, or let go with `.FireAndForget()`, which logs what it throws. Never
  dropped (`VSTHRD110`, `CS4014` fail the build), never discarded with `_ =`: that hides what it threw and says
  nothing about what was meant.
- Work that is tracked by whoever started it, such as `Pending.Start`, returns nothing, so there is no task to
  forget.
- Task-returning methods end in `Async` (`VSTHRD200`); no `async void` (`VSTHRD100`).

### Performance

Asynchronous where work can wait, and fast in the frame loop:

- **No LINQ in the frame loop.** Nothing that runs every frame (`Update`, `Render`, physics, a system's `Run`, a
  render pass) uses LINQ, allocates closures or formats strings: plain loops, delegates made once and kept.
- **Spans for chunks of data.** Pass `Span<T>`/`ReadOnlySpan<T>` instead of slicing arrays onto the heap. A type that
  must stay on the stack is a `ref struct`.
- **`ValueTask` when it is usually done.** A method that completes synchronously most of the time (a cache check)
  returns `ValueTask`/`ValueTask<T>`, not `Task`.
- **Channels for signalling.** Thread-safe hand-offs between threads and event streams use
  `System.Threading.Channels` rather than locks around a queue.
- **One JSON dialect.** JSON is read and written with `AssetJson`'s options; no other options object is made;
  `ChunkReader` is the one `Utf8JsonReader` walker outside Core.
- **Function pointers for hot native calls.** Engine hooks and native libraries called often are wrapped with
  `delegate*` rather than delegates.

### Disposables

What hands out something that ends when disposed (an event or file watch, an ECS observer, a scheduled system, a
registration, an attachment) returns `Subscription`, not `IDisposable`, and whoever keeps it holds a `Subscription`,
so reading the code says what the thing is and why it is kept.

### Gems, systems and scripts

- A gem is `internal sealed class X : IGem` plus the Core interfaces it provides; what it depends on, its own
  settings object included, are constructor parameters, including what it uses of another gem's work (the renderer
  takes the `IWindowRegistry` it draws into): that order is the order gems are made in and, reversed, disposed in.
- Every engine gem is static (`<GemStatic>true</GemStatic>`); hot reload is for a project's gems, which change while
  a game is made.
- A gem frees what it started (a native library, a subsystem) in `Dispose`, once nothing of its own is still running
  on a worker.
- Work over entities is an `ISystem` (`Run(in Frame)`, its `Phase` when not `Update`), added to the `IEcs` and
  kept as a `Subscription`. What is not about entities (pumping events, presenting, loading) is an `IGem` hook.
- A script is a `.cs` asset under a project's `Assets/` or a gem's `Resources/`, its class named like the file;
  a chunk attaches it by the file's id.

### Assets

- An asset type is `public sealed class X : Asset`, with `[AssetFormat(Json)]` or `[AssetFormat(Custom)]` written
  on it; one whose file is its text or its bytes derives from `TextAsset` or `BinaryAsset` instead.
- `[MetaData]` marks what the `.meta` sidecar owns. A loader is an `IAssetLoader<T>` returning `Result`.

### Defaults

- A setting's default is its property initialiser; a file's, its type's initialisers; a component's, its getters
  (`Camera`). Nothing else states it again: no static `Default` instances or constants beside them.
- A type's default asset is `<Type>s/Default.<ext>` (`<Type>s/Default/Default.<ext>` for a folder asset, a
  domain), found by `Assets.Default<T>()`. An asset setting is a path, empty for that default.
- A value an implementation falls back to on its own (the renderer's background with no sky) is defined once in it,
  where it is easy to find and change (`DeferredRenderer`'s `Fallbacks`), never on a Core type.
- The few "nothing" values are `.None` (`Handle.None`, `RenderTarget.MainWindow`, `Allocation.None`).

### Failures

- A `Result` for what can fail on content or the machine: I/O, import, compile, readback.
- Broken content is `Log.Error` and a visible stand-in (the glowing failure surface, the failure cube), never a
  stopped frame.
- What works, only worse, is `Log.Warn`.
- An exception is for a programmer's mistake, and for a gem that cannot exist at all (no GPU device).

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
  the name without it (`Assets.Load` beside `LoadAsync`, `World.Open` beside `OpenAsync`). Test methods are the
  exception: their name is the sentence they check.
- An async method takes a `CancellationToken cancel` as its last parameter and passes it to everything it awaits,
  unless nothing in it could stop. Long work whose share done can be measured (a load with its dependencies, LOD
  generation, opening a domain) also takes an `IProgress<float>? progress`, 0 to 1, just before the token; a
  single step (one file read, a hand-off to a thread, a compile) does not. Both are optional (`= null`,
  `= default`) on a service's public methods and required on interfaces and private helpers.

### Extensions

Extension members use `extension(T x) { }` blocks in an `XExtensions` class, in the part's `Services` folder
when public and `Internal` when not. Related types (a family of enums converted the same way) share one class, one block each.

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
  `IFileSystem.Combine`; take one apart with `Segments`, `Parent`, `Relative` and `IsUnder`. `FileSystem` is the
  one place that knows what a separator is.
- `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine: they know every OS's rules.
- No drive letters, no absolute paths, no checking a string for `..`.
- **An asset path is not an OS path.** It is relative to its asset root and always uses `/`, on every OS.
  `Assets` converts at its boundary; nothing else converts one into the other.

## Disk

- All disk access goes through `IFileSystem`, never `File` or `Directory`.
- The one exception is `Log`: it writes crash files itself, so that it works before any service exists and
  after one has broken.
- Engine output goes under `Project`'s folders (`Logs`, `Cache`, `Screenshots`), never a hardcoded temp, home or
  AppData path.
- File watch events differ per OS: duplicated, coalesced, out of order. Treat one as "something changed here",
  collect them through `FileChanges`, and check what exists.

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
  Anything platform-specific lives in a gem, behind one of Core's interfaces (`IWindow`, `IInput`, `IRendering`),
  guarded by `OperatingSystem.IsWindows()` and its siblings.
- A native library is referenced by its logical name (`"SDL3"`, never `"SDL3.dll"`). Its binaries come from a
  NuGet package per platform, picked by `$(MagicPlatform)` or a condition on `$(MagicRuntime)`.
- No csproj names a runtime identifier. It comes from `Directory.Build.props`: the build machine's own, or
  `-r <rid>` on a project (`Tools/build-all.ps1 -Runtime <rid>` for all of them; a solution refuses it).

## Graphics

- Shaders are HLSL only, compiled by whichever gem provides `IShaderCompiler`, with their `#include`s spliced in
  from the assets first (`Hlsl.Splice`). Backend-specific code lives only in the GPU gem (SdlGpuRenderer).
- A GPU feature is used only if Vulkan, D3D12 and Metal all have it through SDL GPU.

## Scripts and tools

Prefer one cross-platform script over a script per OS: repo scripts are written in a cross-platform scripting
language such as PowerShell (`pwsh`), or are a `dotnet` command. No `.bat`/`.sh` pairs.

## Tests

Tests pass on every supported OS. They touch no disk; a path in an input or an expectation is built with
`Combine`, never written Windows-shaped (`C:\`, `\\`).



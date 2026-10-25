# Code Standards

The rules for C#, Slang and build files in Magic. `.editorconfig` and `BannedSymbols.txt` enforce what a tool can
check; this page covers the rest. Every warning except a TODO comment's fails the build. New code follows these rules,
and old code catches up when you touch it.

## How we write code

Write the engine as data flowing through functions, not objects calling each other.

- **Pipelines, not webs.** Data moves A → B → C. Each step takes values and returns values, and the whole flow reads
  top to bottom in one place. No events firing into handlers that fire more events.
- **Stateless by default.** A function takes what it needs and returns a result. State lives in [four places](#state);
  everything else lasts one call or one frame.
- **Functions over objects.** Records, plain data and static functions. No class hierarchies, virtual methods or
  objects that guard their own fields.
- **Async for anything slow.** Work that can take longer than a frame is an `Async` method with progress and
  cancellation. The frame never waits on it; it picks up the result in a later frame.
- **Shallow.** Flat methods, flat folders, short call chains. If you have to step through five layers to find what
  happens, flatten it.
- **Small.** Make the smallest change that works. No abstraction before a second real use, and no guessing at edge
  cases.

```text
FrameInput ──▶ gem A ──▶ gem B ──▶ gem C ──▶ FrameOutput ──▶ render thread
                 ▲▼         ▲▼         ▲▼
              state A    state B    state C      kept by Core, handed back each frame
```

## Ground rules

- Reuse what exists. When two places do the same thing, one function does it. No helper with a single caller.
- Delete dead code instead of commenting it out. No unused private or internal member, parameter or overload. Public
  API a gem or game could use stays without a caller.
- Leave the code you touch cleaner without changing what it does, and keep the cleanup apart from the fix.
- Use a known pattern only for the problem it solves, and name it in a comment (a pool, a fold).
- These rules come first, then .NET's Framework Design Guidelines. Use current C# and .NET, and one word for one thing
  across the code base.
- Managed dependencies come from NuGet. A package with a known vulnerability, direct or transitive, fails the restore.
- Native libraries are built from source or downloaded as their author publishes them
  ([Native libraries](../src/Bundled/Gems/README.md#native-libraries)). No third-party wrapper, package, binary or
  copied code. Hand-write a binding only where the generator can't, and say so in the gem's README. Vulkan is the
  exception: its API comes from the Vortice.Vulkan package over the system's Vulkan loader.
- No keys, tokens or passwords in code, configuration or workflows. CI reads its own from the repository's secrets.
- Everything runs on every platform Magic targets ([Cross platform](#cross-platform)).

## State

State lives in four places:

1. The ECS's components, written through queries in `Step` and `FixedStep`. A gem never caches them.
2. A gem's state records, one per thread. Core keeps them and hands them back each call, and the gem returns the next
   one.
3. Core's snapshots of assets and settings, read from `FrameInput`.
4. Caches on disk, keyed by what made them.

Nothing else holds a field that changes between frames. The log is the one static. Scripts keep their own fields; they
are the one exception.

- A state record holds immutable collections (`ImmutableArray`, `FrozenDictionary`) and changes through `with`. No
  `List<T>` or other mutable collection inside one.
- Anything with stages (loading, streaming, opening a domain) is a pure `Step(state, inputs)` that returns the next
  state and the actions to run, plus a thin shell that runs the actions and feeds their results back next frame.
- Public and internal data are `PascalCase` properties: `{ get; init; }` for a value set once, `=>` for one derived
  from other data. Interop structs, GPU structs and ECS components keep public fields.
- A private field is `_camelCase` and holds only what the constructor was handed, or what a shell needs to run (a
  channel, a native handle).

## Gems and the frame

- A gem is an `internal sealed` class implementing `IGem<TState, TRenderState>` and the Core interfaces it provides.
- Its constructor takes everything it depends on, settings included. Nothing asks a container for a service later;
  only code that builds objects from constructors takes `IServiceProvider`.
- `Step` and `FixedStep` run on the main thread: the state and `FrameInput` go in, the next state and a `GemOutput`
  come out.
- The constructor, `Render` and `Stop` run on the render thread. `Render` draws from the frame's `FrameOutput` and
  returns the next render state, which holds the gem's GPU resources.
- The two threads share nothing. What the main thread makes reaches the render thread only through `FrameOutput`.
- Gems talk through message structs. Publish one from any thread and every gem reads it in the next frame's
  `FrameInput`. No callbacks, events or watchers.
- Async work a `Step` starts arrives in a later `FrameInput`, or its task sits in the state record and `Step` checks
  `IsCompleted`.
- A gem reads its settings from the frame's snapshot, so changes reach it between frames. Mark a setting it reads only
  at start `[RequireRestart]`.
- A gem gets the command line and project file from Core's `Args` and `App`, never by reading them itself.
- `Stop` frees what the gem started, once none of its work still runs on a worker.
- A gem's csproj states its `Description`, `Version` and `Authors`. Nothing else does.
- Gems depend on Core's interfaces, never on each other.
- A component is an unmanaged struct with public fields that implements `IComponent`, and nothing else.
- Work over many entities is a system, or an `IParallelSystem` for heavy work. Register it on the ECS and keep the
  `Subscription` it returns.
- A script is a `.cs` asset whose class, named like the file, is an `IScript`: an `IBehavior` for one entity, or a
  system script for many. Its constructor takes its `Entity` first if it uses it, then services. Its values are public
  `{ get; set; }` properties, and `Start()` runs once they're set.
- A game is its assets and scripts, never a gem.

## Async

- Anything that can take longer than a frame has this shape, with no defaults:

  ```csharp
  Task<Result<T>> NameAsync(/* inputs */, IProgress<LoadProgress>? progress, CancellationToken cancel);
  ```

  Drop `progress` when there is nothing to count, and `cancel` when nothing in it could stop. Report progress from any
  thread, never lower than last time for the same work.

- A method returning `Task` or `ValueTask` ends in `Async`, private ones too, and nothing else does. Its synchronous
  twin drops the suffix and is written synchronously, never as a wait on the async one.
- Every `await` has `.ConfigureAwait(false)`, except in tests.
- Await every task, keep it to check later, or call `.FireAndForget()`. Never drop it or discard it with `_ =`. No
  `async void`.
- Cancellation ends the task with `OperationCanceledException`, never a failed `Result`.
- Nothing blocks the main or render thread: no `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`, and no waiting on
  the other engine thread. The one allowed wait is at a boundary where every thread it could hold up has ended, with a
  comment saying so.
- A worker hands an engine thread data (a message, a `Channel<T>`, a value written whole), never work. No locks on
  collections two threads share.
- Background work is a task. `Threads` is for the engine's own threads and work that lasts the whole run.
- A timeout is the caller's, through a linked token source.

## Failures and null

- Return a `Result` for anything content or the machine can break: I/O, import, compile, readback.
- Broken content logs `Debugging.Error` and shows a visible stand-in; it never stops the frame. What still works, only
  worse, logs `Debugging.Warn`.
- Throw only for a programmer's mistake or a gem that can't exist (no GPU): `ArgumentException`,
  `InvalidOperationException` or `NotSupportedException`, never `Exception` itself. Use the throw helpers
  (`ArgumentNullException.ThrowIfNull`, `ObjectDisposedException.ThrowIf` and their siblings).
- Never catch and drop. Log what you catch with what failed and on what, or `throw;`. Catch `Exception` only where one
  failure mustn't stop the run (a gem's hook, a loader, a forgotten task); elsewhere catch what you expect or filter
  with `when`.
- A log line says what happened and to what, once. Anything that repeats every frame is `Debugging.Verbose` or a metric.
- `Debugging.Assert` is for impossible state and ends the process with a crash file.
- No null-forgiving `!`; it and every nullable warning fail the build. Don't make what is always there nullable: pass it
  in, make it `required`, or group the optional parts into one object.
- Handle a missing value where you read it, with a guard clause, `is not { } value`, `??` or a `throw` saying what's
  missing. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a check proves.

## Performance

- Outside the frame loop, write the simple version first and optimise only after measuring in a Debug build.
- Nothing that runs every frame uses LINQ, closures or string formatting. Use plain loops and delegates made once.
- Pass `Span<T>` and `ReadOnlySpan<T>`, not array slices. A type that must stay on the stack is a `ref struct`.
- Return `ValueTask` from a method that usually finishes synchronously.
- Loading and building run as tasks and hand their results to the main thread in one batch.
- Call frequent native functions through function pointers (`delegate*`).

## Structure

### Visibility

- Public is only for what a gem or game script uses. Everything else is `internal` or private. Container and host
  plumbing are never public, and nothing is public for a test's sake ([Tests](../tests/README.md)).
- A type only one gem uses lives in that gem, internal. Core holds the types gems share.
- Add an interface only to swap a gem or to reach outside the process (disk, GPU, window), never for a test.
- No type that only forwards calls to another.

### Folders and files

- A namespace is the project's root namespace (`Magic` for Core, `Magic.<Name>` otherwise) plus its folders.
- Core puts each type in the first folder that fits: the root (what starts the run), `Internal` (not public),
  `Interfaces`, `Attributes`, `Extensions`, `Contexts` (frame and run data, and every other record, enum and struct),
  then `Services`.
- A folder exists only once it holds something. Split a big one into a subfolder per area, never named like a type in
  it.
- A gem is flat: every file at its root, in `Magic.<Gem>`. Its `Resources/` holds the assets and scripts only it uses.
- Built-in assets every project can use go in `src/Bundled/Resources/`.
- Name a file for its main type. Small types that belong with it may share the file.
- Extension members go in `extension(T x) { }` blocks in an `XExtensions` class. Related types share one class.

### Assets

- An asset type is a sealed class implementing `IAsset<T>` of itself. It holds what its users read, not file details,
  and states its file extensions as a static. Only Core's `Assets` sets its handle.
- An asset type gems share is Core's. One only a gem reads is that gem's.
- JSON assets implement `IJsonAsset<T>` and text ones `ITextAsset<T>`. Other formats need a gem's `IAssetLoader<T>`
  returning a `Result`. The `.meta` file holds the import settings.
- A type read from a file is plain data: properties only, no JSON attributes, no I/O. Separate code reads it.
- A key the file must have is `required { get; init; }`. One with a default is `{ get; set; }`, the default as its
  initializer and nowhere else. What code sets, not the file, has no public setter.
- Read and write all JSON with [Core's](../src/Core/README.md#assets) one dialect. No per-type options.
- Every asset type has a default asset, `<Type>s/Default.<ext>` (`<Type>s/Default/Default.<ext>` for a folder asset).
  An asset setting is a path, empty for that default.
- Refer to an asset by its typed handle, never a path, except in code that starts from a name.
- A "nothing" value is a static `None` on its type. An implementation's own fallback lives once in it, never on a Core
  type.

## Style

`tools/Scripts/Format.ps1` and `.editorconfig` decide member order, spacing and line wrapping
([Format.ps1](../tools/README.md#formatps1)). When the formatter wraps a line badly, reshape it by hand.

### Methods

- A method reads top to bottom: guard clauses, setup with `using` declarations, the main path kept flat with early
  `continue` and `return`, the result, then local functions.
- Keep methods small. Split a long run of steps into methods called in order, so the caller reads like the pipeline. A
  method that is one big `switch` stays whole.
- Nothing nests three deep: use a guard clause, an early `continue`, or a method named for the step.
- Two values: `?:`. More: a switch expression or early returns. Never a `?:` inside a `?:`.
- A one-statement body on one line uses `=>`. A primary constructor only keeps its parameters; one that does work is
  explicit.
- One-statement `if` and `else` bodies have no braces. Longer bodies, wrapped conditions and every loop do, and if one
  branch needs braces, all do. A blank line goes before `return`, `continue`, `break` or `throw` unless it opens its
  block.

### Naming

- A name reads well as `Class.Member(arguments)`: `Assets.Find<Texture>(path)`, `App.Quit(reason)`. Action first,
  subject second (`ReadDomain`), naming what it acts on even if that's longer. If it doesn't make sense without its
  summary (`Process()`, `_scratch`), rename it.
- Each verb means one thing:
  - `Get` never fails, but may return null when there is nothing.
  - `Find` searches and may find nothing.
  - `Try` returns whether it worked, with the result in an `out` or a `Result`. `TryGet` is a lookup that can fail.
  - `Create` brings something new into being, never `Make`. `To` converts. `Parse` and `Format` do what they say.
  - `Register` hands something to a service and returns the `Subscription` that takes it back. `Add` and `Remove`
    belong to collections.
  - `Is`, `Has` and `Can` return a bool.
- Hooks and entry points keep general verbs: `Step`, `Render`, `Start`, `Run`.
- A method that returns a value is never a bare noun (`Amount`, `RowOf`).
- A number that's a time ends in its unit (`FixedStepSec`, `_settleMs`). A `TimeSpan` doesn't (`_settleTime`).
- No single-letter names except loop counters, `x`/`y`/`z` and formula coefficients. `ctx`, `sb`, `dt` and `ex` are
  fine. A local keeps one meaning and never shadows a type.
- A reusable buffer has one purpose and is named for it.
- Keep .NET's idioms: static factories (`Result.Success`), fluent builders, immediate-mode widgets (`IDebugUI.Button`).
- Test methods read as the sentence they check.

### Comments

- XML doc comments on types and non-obvious members, saying why, not what. `<summary>` and `</summary>` go on their own
  lines.
- Put a `//` comment above a block whose purpose isn't obvious at a glance (a loop doing two things, a calculation, an
  order that matters, a workaround). It covers the block down to the next blank line. Never restate the line.
- No divider comments and no `#region`.

## Shaders

- Slang 2026, warnings as errors. One module a file, named by its path (`module "Core/Heap";`). The gem that provides
  `IShaderCompiler` compiles shaders and loads their imports from the assets.
- Read every resource through the heap's references in `Core/Heap.slang`; never declare one.
- Name a pass for its job, a verb and what it acts on (`CullClusters`, `ShadeLighting`), in its feature's module. Its
  constants are uniform parameters: the pass header, then one struct.
- Names follow [Naming](#naming):
  - A function returning a value starts with `Get` (`GetLuminance`), or `Load`/`Sample` for memory reads, `To` for
    conversions, `Encode`/`Decode` for packing and `Create` for a struct it builds. A contract's work is `Evaluate`,
    `Shade` or `Apply`. `Pow2`, `Max3` and the like read as the intrinsics beside them.
  - A bool starts with `Is`, `Has` or `Can` (`IsHidden`, a field's in camel case: `isMirrored`).
  - A contract is `I` plus a noun (`ISurface`), and what it's handed is the noun plus `Inputs` (`SurfaceInputs`). A
    struct shared with C# ends in `Constants`, or starts with `Gpu` for a table's row. A heap reference ends in `Ref`,
    stage-to-stage data in `Varyings`, a draw's outputs in `Targets`. A set of codes is an `enum`.
- Put what a struct does on it as extensions (`instance.ToCameraRelative(position)`), so a struct shared with C# keeps
  only its fields. Return a struct rather than `out` parameters.
- A struct C# reads or writes holds only 32-bit scalars, their vectors, row-major matrices, heap references and such
  structs. Mark its C# twin `[ShaderStruct]`; the renderer checks the two match when it starts.
- A contract between shaders is an interface (`ISurface`, `ISky`, `ILight`, `IPixelEffect`), and a pass that works with
  any is generic over it.
- Totals across a dispatch run in its last group (`IsLastGroup`), not in a one-group pass after it.
- Use wave intrinsics only where the result can't depend on wave size (32 or 64) or the order is already arbitrary. No
  16-bit types, and no runtime-indexed resource arrays but the heap's.
- Guard values where they go wrong: clamp roughness with the library's helper, keep n·v above 0, normalise with the
  helper that allows zero, and use `SampleGrad` or `SampleLevel` inside a branch.
- A compute pass has no implicit derivatives. Code it may run takes them as inputs, as `SurfaceInputs` and
  `SkyInputs` do.
- Few variants: a generic for what changes the pipeline, a `[branch]` on a draw-wide value for the rest.
- The BRDF, lights and their units, and the shading models live once in the shared
  [shader library](../src/Bundled/Resources/Shaders/README.md). Every renderer shades with them.
- Write a technique from its paper and name the paper in a comment. No third-party shader code.

## Cross platform

All code runs on Windows, Linux and macOS.

- **Paths.** Build them with `IFileSystem.Combine` and split them with `IFileSystem.GetParent`; never touch separators.
  `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine. No drive letters, absolute paths or
  checks for `..`. An asset path is relative to its root and uses `/`; only Core's `Assets` turns it into an OS path.
- **Case.** File names in code, assets, `#include`s, csproj and `.meta` files match the disk exactly. Two files never
  differ only by case.
- **Disk.** All disk access goes through `IFileSystem`, never `File` or `Directory`; only `Debugging` is exempt. Engine
  output goes next to Magic in a folder per kind (`Logs`, `Screenshots`), never a temp, home or AppData path. The cache
  is the one exception ([The cache](../src/Core/README.md#the-cache)). Treat a file watch event as "something changed
  here": collect them in one place and check what exists.
- **Text.** Write files and generated shader source with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM.
  Accept `\r\n` and a BOM when reading. Parse and format numbers and dates culture-invariantly.
- **Platform code.** No OS APIs in Core: no P/Invoke, registry, `.exe`/`.so`/`.dylib` names or `OperatingSystem.Is*`.
  Platform code lives in a gem behind a Core interface, guarded by `OperatingSystem.IsWindows()` and its siblings.
- **Native libraries.** Reference one by its logical name (`"name"`, never `"name.dll"`). One gem declares it and ships
  its binary under `runtimes/<rid>/native/`. No csproj names a runtime identifier.
- **Scripts.** Repository scripts are one PowerShell file each, for `pwsh` and Windows PowerShell 5.1, or a `dotnet`
  command. No `.bat`/`.sh` pairs.
- **Graphics.** Draw through `IGpuDriver`: a gem records commands as plain data and the GPU provider turns them into API
  calls; only the GPU provider has backend code. Draw into a render target (a `GpuTexture`), asking its format and size;
  never assume a window. Use a GPU feature only if Vulkan 1.3 has it on every GPU Magic runs on, a Mac's MoltenVK and
  KosmicKrisp included. Anything more is optional, with a path that works without it.

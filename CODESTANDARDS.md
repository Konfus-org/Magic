# Code Standards

Rules for all C#, HLSL and build configuration. Every warning is a build error. `.editorconfig` holds the style rules
and `BannedSymbols.txt` the banned APIs. New code follows every rule; bring existing code up to them as you touch it.

## Policies

- Make the smallest, simplest change that works. No abstraction before a second real use, no guessing at edge cases.
- Use a known pattern only for the problem it solves, and name it in a comment (a double-buffered snapshot, a pool).
- Reuse what exists. When two places do the same thing, one method does it. No helper with a single caller.
- Delete stale code instead of commenting it out. No unused member, parameter or overload.
- Leave the code you touch cleaner, without changing behaviour, and keep the cleanup apart from the fix.
- Follow these rules, then .NET's Framework Design Guidelines. Use one word for one thing across the code base.
- Use current C# and .NET. Follow `.editorconfig` ([Format.ps1](tools/README.md#formatps1)).
- All code runs on every platform Magic targets ([Cross platform](#cross-platform)).
- Managed dependencies come from NuGet. A package with a known vulnerability, direct or transitive, fails the restore.
- Native libraries are built from source, or downloaded as their author publishes them
  ([Native libraries](src/Bundled/Gems/README.md#native-libraries)). No third-party wrapper, package, binary or copied
  code. Hand-write a binding only where the generator can't, and say so in the gem's README. The one exception is
  Vulkan, whose API comes from the Vortice.Vulkan package over the system's Vulkan loader.
- No keys, tokens or passwords in code, configuration or workflows. CI reads its own from the repository's secrets.

## Architecture

### Visibility

- Make public only what other parts use, such as a gem or a game script. Everything else is `internal` or private.
- The container and host plumbing are never public.
- A type only one gem uses lives in that gem, internal. Core holds the types gems share.
- A member is private, then internal once its project needs it, and public only on a public contract or interface.
- Nothing is public for a test's sake ([Tests](tests/README.md)).
- Add an interface only to swap a gem or to reach outside the process (disk, GPU, window), never just for a test.
- No type that only forwards calls to another.

### Folders

- A namespace is the project's root namespace (`Magic` for Core, `Magic.<Name>` otherwise) plus its folders.
- Core puts each type in the first folder that fits: the root (what starts the run), `Internal` (not public),
  `Interfaces`, `Attributes`, `Extensions` (`XExtensions` classes), `Contexts` (run and frame state for gems,
  subscriptions, and every other enum, record and struct), then `Services` (services only).
- A folder holds only what its rule says, and exists only once it holds something. Agree a new one for a misfit.
- Split a big folder into a subfolder per area, never named like a type in it. What fits no one area stays on top.
- A gem is flat: every file at its root, in `Magic.<Gem>`. Its `Resources/` holds its assets and scripts, not code.
- The engine's built-in default assets go in `src/Bundled/Resources/`.
- Name a file for its main type. Small types that belong with it may share its file; others get their own.
- Extension members go in `extension(T x) { }` blocks in an `XExtensions` class. Related types share one class.

### Gems, systems and scripts

- A gem is `internal sealed class X : IGem` plus the Core interfaces it provides.
- A gem takes all it depends on, settings included, as constructor parameters and keeps them.
- Never get a service from a container later. Only code that builds objects from constructors takes `IServiceProvider`.
- A gem's csproj states its `Description`, `Version` and `Authors`. Nothing else does.
- A gem gets the command line and project file from the `Args` and `App` services, never by reading them itself.
- A gem's options come from its settings object, which is plain data like a file type ([Assets](#assets)).
- A gem follows setting changes between frames. Mark a setting it reads only at start `[RequireRestart]`.
- A gem frees what it started in `Dispose`, once none of its work still runs on a worker.
- The constructor, `Dispose` and `Render` run on the render thread; the other hooks on the main thread.
- Each thing a gem owns is used on one thread. The other thread hands it data, taken in at the start of a hook.
- What a gem owns on the main thread ends in `Stop`, before `Dispose`.
- `LateUpdate` fills a copy for `Render`, `Sync` only swaps the copies, and `Render` reads only its copy.
- A component is an unmanaged struct with public fields that implements `IComponent`, and nothing else.
- Work over entities is an `ISystem<T1>` (up to four components; `ISystem` for all), or an `IParallelSystem` for many.
  State its `Phase` when it isn't `Update`. Register it with `World.Register` and keep the `Subscription`.
- Work that isn't about entities (pumping events, presenting, loading) is an `IGem` hook.
- Tell other gems what happened with a message struct on `Events`. A listener keeps its `Subscription`.
- A script is a `.cs` asset whose class, named like the file, is an `IScript`: an `IBehavior` for one entity each
  frame, or a system script for many.
- A game is its assets and scripts, never a gem.
- A script's constructor takes its `Entity` first if it uses it, then services, and only keeps them. A pipeline
  script takes services only. A script's values are public `{ get; set; }` properties. `Start()` runs once they're set.

### Assets

- An asset type is a sealed class implementing `IAsset<T>` of itself. It holds what its users read, not file details.
- It states its file extensions as a static. Only Core's `Assets` sets its handle, once.
- An asset type gems share is Core's. One only a gem reads is that gem's.
- A type stored as JSON implements `IJsonAsset<T>`, and one stored as text `ITextAsset<T>`.
- Other formats need a gem's `IAssetLoader<T>` returning `Result`. The `.meta` file holds the import settings.
- Name an asset by its typed handle, never a path, except in code that starts from a name.
- A type read from a file is plain data: properties only, no JSON attributes, I/O or `Open`. Separate code reads it.
- A key the file must have is a `required` `{ get; init; }` property. One with a default is `{ get; set; }`. What code
  sets, not the file, has no public setter.
- Read and write all JSON with [Core's](src/Core/README.md#assets) one dialect. No per-type setup or options objects.
- State each default once, as a property initializer (a component's in its getters). No static `Default` instances
  or constants next to them.
- Every asset type has a default asset, `<Type>s/Default.<ext>` (`<Type>s/Default/Default.<ext>` for a folder asset).
  An asset setting is a path, empty for that default.
- Define an implementation's own fallback (the renderer's background with no sky) once in it, never on a Core type.
- A "nothing" value is a static `None` on its type.

### Disposables

- Return `Subscription`, not `IDisposable`, for anything that ends when disposed, and hold it as a `Subscription`.
- Keep several that live as long as their owner in a `SubscriptionList`.

## Correctness

### Null

- No null-forgiving `!` and no `// !` comment. The build fails on it and on every nullable warning.
- Don't make what is always there nullable: pass it in, make it `required`, or group optional parts into one object.
- Handle a missing value where it's read: a guard clause, `is not { } value`, `??`, or a `throw` saying what's missing.
- Use `[MemberNotNullWhen]` and `[MaybeNullWhen]` to tell the compiler what a check proves.
- Throw through the named helpers (`ArgumentNullException.ThrowIfNull` and its siblings,
  `ObjectDisposedException.ThrowIf`), and check a string that must say something with `string.IsNullOrWhiteSpace`.

### Async

- Every `await` has `.ConfigureAwait(false)`, except in tests.
- Await every task, keep it to await later, or call `.FireAndForget()`. Never drop it or discard it with `_ =`.
- Use tasks for async and background work. `Threads` is for the engine's threads, work that lasts the whole run, and
  flows that must await a step on an engine thread.
- A worker hands an engine thread data (a `Channel<T>`, or a reference written whole), never work. The hook that owns
  the structure takes it in at its start. No locks on collections two threads share.
- Nothing blocks waiting on the main or render thread. Nothing there or in the frame loop blocks on a task
  (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`).
- Implement a synchronous API synchronously, not as a wait on its async twin.
- The one allowed wait is at a boundary where every thread it could hold up has ended, with a comment saying so.
- No `async void`.

### Failures

- Return a `Result` for what can fail on content or the machine: I/O, import, compile, readback.
- Broken content logs `Debugging.Error` and shows a visible stand-in. It never stops the frame.
- What works, only worse, logs `Debugging.Warn`.
- Throw only for a programmer's mistake, or a gem that can't exist (no GPU): `ArgumentException` for a bad argument,
  `InvalidOperationException` for a call at the wrong time, `NotSupportedException` for what this implementation
  doesn't do. Never `Exception` itself.
- Never catch and drop. Log what you catch with `Debugging.Error`, saying what failed and on what, or `throw;`.
- Catch `Exception` only where one failure must not stop the run (a gem's hook, a loader, a task let go), and log it.
  Elsewhere, catch the exception you expect or filter with `when`.
- A log line says what happened and to what, once. What repeats every frame is `Debugging.Verbose` or a metric.
- `Debugging.Assert` is for impossible state; it ends the process with a crash file. API misuse stays an exception.

### Performance

- Outside the frame loop, write the simple form first. Optimise only after measuring in a Debug build.
- Work asynchronously where the work can wait.
- Nothing that runs every frame uses LINQ, closures or string formatting. Use plain loops and delegates made once.
- Pass `Span<T>` and `ReadOnlySpan<T>`, not array slices. A type that must stay on the stack is a `ref struct`.
- Return `ValueTask` from a method that usually completes synchronously.
- Work over many entities is a parallel system. Loading and building are tasks that hand their results to the main
  thread in one batch. Drawing is the render thread's.
- Call frequent native functions through function pointers (`delegate*`).

## Style

### Type layout

- Members go in StyleCop's order, which `tools/Format.ps1` applies: fields (constants first), constructors, events,
  properties and indexers, extension blocks, operators, methods, then nested types (delegates, enums, interfaces,
  structs, classes). One blank line between members.
- Within each kind: public, internal, then private, and static before instance.
- Types that share a file follow the nested types' order. `Dispose` is the first public method.
- Interface members are public, placed with their kind. Use `IX.Member` only for a name clash, under the public one.
- Use a primary constructor when it only keeps its parameters, and an explicit one when it does work.
- Keep related members together, and related fields under a comment saying what they're for.

### Lines

- A line of code fits within `max_line_length`. `tools/Format.ps1` wraps one that doesn't.
- A list of arguments or parameters that doesn't fit, and every list that spans lines, has each item on its own
  line, one indent in, starting on the line after the opening parenthesis.
- When a line breaks at an operator, `?:` or `=>`, that starts the next line. An `=` ends its line.
- Split a long string by hand, the next part starting with `+`.

### State

- Public and internal state are `PascalCase` properties, never fields.
- Use `{ get; }` set in the constructor for a value set once, `{ get; set; }` or `{ get; private set; }` for one that
  changes, and `=>` for one derived from other state. Never write `get { return ...; }`.
- Private state is a `_camelCase` field at the top of the type, whatever it holds. If reading or writing it has to do
  something, use a method.
- Interop and data structs (`[StructLayout]`, GPU structs, ECS components) keep their public fields.

### Methods

- A method reads top to bottom, with a blank line between: guard clauses, setup with `using` declarations, the main
  path kept flat with early `continue` and `return`, the result, then local functions.
- Keep a method small. Split a long sequence of steps into methods called in order, with a summary where a step's
  reason isn't obvious. Keep a method that is one big `switch` whole.
- Turn a loop body nested three deep into an early `continue` or a method named for the step.
- Put a blank line before a `return`, `continue`, `break` or `throw`, unless it opens its block or ends a switch
  section. A run of early-out `if`s shares one blank line before them.
- A method, local function or operator whose body is one statement on one line uses `=>`.
- An `if` or `else` with a one-statement body has no braces. The body goes on the header's line when it fits, or on
  the next line, one indent in. An `else` starts its own line.
- A body of more than one statement, a condition too long for its line, and every loop have braces on their own
  lines. If one branch needs braces, all do.
- Two values: `?:`. More: a switch expression or early returns. Never a `?:` inside a `?:`.
- Turn an `if` that ends a method or loop iteration and holds the rest of it into a guard clause.

### Naming

- A name reads well as `Class.Member(arguments)`: `Assets.Find<Texture>(path)`, `App.Quit(reason)`. If it
  doesn't (`Process()`, `_scratch`), name the object: what is processed, what is held.
- Put the action first and its subject second, as English does (`ReadDomain`, `ShowDomain`). Name what a member acts
  on, even at some length: read the name as if it had no summary.
- `Get` never fails; it may return null when the thing has none (`Assets.GetPath` of a handle with no file). `Is`,
  `Has` and `Can` answer a question with a bool, keys in files included. `Try` returns whether it worked, with the
  result in an `out` parameter or a `Result`. `TryGet` is a lookup that can fail.
- Other verbs: `Create` for what brings a new thing into being, never `Make`; `Parse`; `Format`; `Find`, which
  searches and may find nothing; `To` for a conversion.
- A number that is a time ends in its unit, abbreviated (`FixedStepSec`, `_settleMs`). A `TimeSpan` names what time
  it is, with no unit (`_settleTime`).
- `Register` hands something to a service and returns the `Subscription` that takes it back. `Add` and `Remove`
  belong to a collection.
- A method that returns a value is never a bare noun (`Amount`, `RowOf`).
- Keep .NET's idioms: static factories (`Result.Success`), fluent builders, immediate-mode widgets (`IDebugUI.Button`),
  and a collection's `Add` and `Remove`.
- A reusable buffer has one purpose, and is named for it.
- No single-letter names except loop counters, `x`/`y`/`z` and formula coefficients. `ctx`, `sb`, `dt`, `ex` are fine.
- A local keeps one meaning and never shadows a type.
- Methods that return `Task` or `ValueTask` end in `Async`, private ones too, and a synchronous twin drops it (`Read`
  and `ReadAsync`). Test methods are named as the sentence they check.
- An async method takes a `CancellationToken cancel` last and passes it on, unless nothing in it could stop. Long work
  with measurable progress also takes an `IProgress<float>? progress` (0 to 1) just before the token. Both are
  optional on a service's public methods, and required on interfaces and private helpers.

### Comments

- XML doc comments on types and non-obvious members. Say why, not what.
- Put a `//` comment above code whose purpose isn't obvious at a glance (a loop doing more than one thing, a
  calculation, an order that matters, a workaround), saying what it does and, if needed, why. It covers the block
  below it, up to the next blank line. Never write a comment that restates its line.
- `<summary>` and `</summary>` go on their own lines, even for a one-line summary.
- Wrap a comment at `max_line_length`, at a word. No divider comments (`// ----`) and no `#region`.

## Shaders

- HLSL 2021 only, Shader Model 6.0 with wave intrinsics, warnings as errors. The gem that provides `IShaderCompiler`
  compiles shaders and loads their `#include`s from the assets.
- Bind resources with the macros in `Include/Bindings.hlsli`, never by register. Its header lists the conventions.
- Use wave intrinsics only where the result can't depend on the wave size (32 or 64), or the order is already
  arbitrary. No 16-bit types and no runtime-indexed resource arrays.
- Guard a value where it can go wrong: clamp roughness with the library's helper, keep n·v above 0, normalise with
  the helper that allows zero, and use `SampleGrad` or `SampleLevel` inside a branch.
- Few variants: a define for what changes the pipeline, a `[branch]` on a value the whole draw shares for the rest.
- The BRDF, the lights and their units, and the shading models live once, in the shared
  [shader library](src/Bundled/Resources/Shaders/README.md). Every renderer shades with them.
- Write a technique from its paper and name the paper in a comment. Copy in no third-party shader code.

## Cross platform

Write all code to run on Windows, Linux and macOS.

### Paths

- Build a path with `IFileSystem.Combine` and split it with `IFileSystem.GetParent`. Never touch separators directly.
- `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine.
- No drive letters, no absolute paths, no checking a string for `..`.
- An asset path is relative to its asset root and always uses `/`. Only Core's `Assets` turns it into an OS path.
- File names in code, assets, `#include`s, csproj and `.meta` files match the disk exactly, case included. Two files
  never differ only by case.

### Disk

- All disk access goes through `IFileSystem`, never `File` or `Directory`. Only `Debugging`, the log, is exempt.
- Engine output goes next to Magic in a folder per kind (`Logs`, `Screenshots`), never a temp, home or AppData path.
  The cache is the one exception ([The cache](src/Core/README.md#the-cache)).
- Treat a file watch event as "something changed here": collect them in one place and check what exists.

### Text

- Write files and generated shader source with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM. Accept
  `\r\n` and a BOM when reading.
- Parse and format numbers and dates culture-invariantly.

### Platform code

- No OS APIs in Core: no P/Invoke, registry, `.exe`/`.so`/`.dylib` names or `OperatingSystem.Is*`.
- Platform code lives in a gem behind a Core interface, guarded by `OperatingSystem.IsWindows()` and its siblings.
- Reference a native library by its logical name (`"name"`, never `"name.dll"`). One gem declares it, and its binary
  ships with that gem under `runtimes/<rid>/native/`.
- No csproj names a runtime identifier.
- Repository scripts are one PowerShell file each, for `pwsh` and Windows PowerShell 5.1, or a `dotnet` command. No
  `.bat`/`.sh` pairs.

### Graphics

- Draw through `IGpu`: a gem records commands as plain data, and the GPU provider turns them into API calls.
  Backend-specific code lives only in the GPU provider.
- Draw into a render target, a `GpuTexture`, asking its format and size. Never assume a window.
- Use a GPU feature only if Vulkan 1.3 has it on every GPU the engine runs on, a Mac's MoltenVK and KosmicKrisp
  included. Anything more is optional, with a path that works without it.

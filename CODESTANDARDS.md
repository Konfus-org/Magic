# Code Standards

The rules every line of C#, HLSL and build configuration in this repository follows, and why. How the engine works is
in [Core's README](src/Core/README.md); this document says only what code must do. The build enforces what it can:
every compiler, analyzer, MSBuild and NuGet warning is an error, `.editorconfig` holds the rules, `BannedSymbols.txt`
the APIs the build refuses, each with what to use instead, and `Directory.Build.props` checks that every namespace
matches its folder. New code follows every rule here; existing code is brought to them as it is touched
([Policies](#policies)).

## Policies

- **The smallest change that does the job.** Keep a change isolated, and prefer the simplest, most direct solution:
  no over-engineering, no abstraction until a second real use needs it, no predicting edge cases. Complexity is added
  only when a specific problem requires it. A known pattern is used where the problem is the one it solves, never
  reached for first, and the comment names it (a double-buffered snapshot, a pool), so a reader who knows the pattern
  reads faster.
- **Duplication.** Reuse what exists before writing more, and where two places do the same thing, one method does it.
  But no single-use helper: a method called from one place is written at that place.
- **Housekeeping.** Permanently delete stale definitions instead of leaving commented placeholders, and add no unused
  member, parameter or overload for a caller that does not exist yet.
- **Leave it cleaner.** The code a change touches is in better shape after it than before. Quality goes one small
  tolerated flaw at a time, and the next reader pays for each one left behind. The cleanup never changes behaviour and
  is told apart from the fix in the review, so each can be judged on its own.
- **Conventions.** These rules first, then .NET's own conventions (the Framework Design Guidelines) where these are
  silent, so the code reads as any C# developer expects. One word for one thing across the code base: what is
  `Cached` here is not `FromCache` there.
- **Modern C#.** Use current C# and .NET, and follow the `.editorconfig` (the build enforces most of it). The target
  moves with `global.json`, so what is current is what
  [.NET's](https://learn.microsoft.com/en-us/dotnet/core/whats-new) and
  [C#'s](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new) what's-new pages say it is.
- **Portable.** All code runs on every major platform; see [Cross platform](#cross-platform).
- **Dependencies.** Third-party managed libraries come from NuGet, and a package with a known vulnerability, direct
  or transitive, fails the restore (`NuGetAudit`). A native library is declared to the build, which clones its
  repository at a pinned commit, builds it for the machine and generates its C# API from the headers
  ([Gems](src/Bundled/Gems/README.md#native-libraries)). No third-party wrapper, package, prebuilt binary or copied
  code stands in for that, and no binding is written by hand except where the generator cannot express the API (a
  COM interface), which the gem that needs it says in its README.
- **Secrets.** Nothing secret is in the repository: no key, token or password in code, configuration or a workflow.
  CI reads its own from the repository's secrets.

## Architecture

### Visibility

Public is for what other parts use; everything else is `internal` (or private), wherever it lives. A Core type is
internal unless a gem or a game script needs it: the container and other host plumbing never are. A type only one gem
uses lives in that gem, internal; Core holds the types gems share. Tests see only what is public (no
`InternalsVisibleTo`): nothing is public for their sake, a member only its own type uses is private, and what is not
public is not tested ([Tests](tests/README.md)).

The same holds member by member, from the bottom up: a member is private until another type in its project needs
it, then internal, and public only on a public type's contract or to implement an interface. A member of an
internal type is never public except where an interface it implements requires it, so `public` always means "part
of a contract", and a reader can tell a type's surface from its workings at a glance.

An interface exists so that a gem can be swapped (Core's contract between gems) or for what reaches outside the
process (the disk, the GPU, a window); nothing gets one so that a test can fake it. No type wraps another that already
does the job to forward to it: a layer whose members each call one member of the next is a layer with no reason.

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
take in at a glance. Its `Resources/` folder holds the gem's own assets, scripts among them, never its code. The
engine's built-in default assets, the ones that come with Magic whatever gems a project loads, are
`src/Bundled/Resources/`.

A file is named for its main type. Small types that belong with it and go in the same folder can share its file (an
enum one of its properties takes, the record that goes with it); a type that stands on its own has a file of its own.
Extension members use `extension(T x) { }` blocks in an `XExtensions` class, filed as above; related types (a family
of enums converted the same way) share one class, one block each.

### Gems, systems and scripts

- A gem is `internal sealed class X : IGem` plus the Core interfaces it provides. What it depends on, its own
  settings object included, are constructor parameters, including what it uses of another gem's work (a renderer
  takes the window it draws into): that order is the order gems are made in and, reversed, disposed in. What it
  needs arrives there and is kept, so what a type needs is read from its signature and the order to make gems in
  stays decidable. Nothing asks the container or a provider for a service later, with one exception: what makes
  objects at run time from their constructors' parameters, as the gem that runs scripts makes each script from the
  services its class asks for, takes `IServiceProvider` for that. Its csproj says what it is, in the SDK's
  `Description`, `Version` and `Authors`, and nothing else declares those.
- A gem reads nothing of the run itself: what it needs of the command line or the project file arrives as the `Args`
  and `Project` services, and what it can be told arrives as its settings object, plain data like a file type
  ([Assets](#assets)). Its settings can change while running, between frames, and a gem follows each: it reads it
  where it is used, or compares it each frame with what it last applied. One it can only read as it starts is
  `[RequireRestart]`, and changing it starts the engine again.
- A gem frees what it started (a native library, a subsystem) in `Dispose`, once nothing of its own is still running
  on a worker.
- A gem's constructor, `Dispose` and `Render` run on the render thread; `Update`, `FixedUpdate`, `LateUpdate`, `Sync`
  and `Stop` on the main thread. What a gem owns is used on one of them: what the other has for it is data the owner
  takes in at the start of its hook, and what of the main thread's a gem owns ends in `Stop`, before `Dispose`.
  `LateUpdate` prepares what `Render` will read, into a copy `Render` is not reading; `Sync` swaps the two and
  nothing more, as every gem waits on it; `Render` reads only that copy, since the main thread is changing everything
  else.
- A component is an unmanaged struct with public fields that implements `IComponent`, and nothing else: declaring it
  is all it takes for the world to hold it and a file to name it ([Components](src/Core/README.md#components)).
- Work over entities is a system that names the components it looks at, up to four (`ISystem<T1>` and on; `ISystem`
  for every entity), and an `IParallelSystem` when it is work over many entities. Its `Phase` is stated when not
  `Update`: `Update`, `FixedUpdate` or `LateUpdate`. It is added to the world (`World.Add`) and kept as a
  `Subscription`. What is not about entities (pumping events, presenting, loading) is an `IGem` hook.
- What a gem tells others has happened is a message, a struct, on `Events`; whoever listens watches it and keeps the
  `Subscription`.
- A script is a `.cs` asset under a project's `Assets/`, a gem's `Resources/` or `src/Bundled/Resources/`, its class
  named like the file and an `IScript`: an `IBehavior` for what one entity does each frame, and a system script
  (`ISystemScript<T1>`, `IParallelSystemScript<T1, T2>`, …) when it works over many. A game is its assets and that
  code, never a gem of its own. A script's constructor takes the `Entity` it is attached to first, then what it asks
  for as a gem's does, and only keeps them: no work in a constructor, as its values are not set yet. Its values are its
  public `{ get; set; }` properties, and `Start()` is where it begins, with them set. A pipeline script runs on no
  entity, so its constructor takes services alone ([Scripts](src/Core/README.md#scripts)).

### Assets

- An asset type is a sealed class implementing `IAsset<T>` of itself: what its users read, never how its file was
  read. It states its file extensions as a static, so no code names them, and has a `Handle { get; init; }`, the one
  thing on it Core's `Assets` sets rather than its file. A type gems share is Core's; one only its gem reads is that
  gem's.
- A type whose file is itself as JSON implements `IJsonAsset<T>`, and one whose file is its text `ITextAsset<T>`:
  Core reads both. Any other format is an `IAssetLoader<T>` a gem provides by implementing it, returning `Result`, and
  what the `.meta` sidecar holds beside the id is its import settings.
- Components, assets and the project name an asset by its typed handle, never by a path: a path is for code that
  starts from a name.
- A type read from a file is plain data: properties, and no JSON attributes, I/O or `Open`. Turning bytes into it and
  finding its file are separate code beside it, and nothing else repeats its shape. Its properties follow the file
  rather than [State](#state)'s constructor rule: a key the file must have is a `required` property with
  `{ get; init; }`, and one with a default is `{ get; set; }` with the default as its initialiser. What code sets
  rather than the file has no public setter, so it is never read from a file.
- One JSON dialect: everything is read and written in [Core's](src/Core/README.md#assets), which reaches any type by
  reflection, so nothing is declared for a type to be read or written and no other options object is made.
- A default is stated once: a setting's is its property initialiser; a file's, its type's initialisers; a script's,
  its properties' initialisers; a component's, its getters (`Camera`). No static `Default` instances or constants
  beside them. Every asset type has a default asset, `<Type>s/Default.<ext>` (`<Type>s/Default/Default.<ext>` for a
  folder asset, a domain), and an asset setting is a path, empty for that default. A value an implementation falls
  back to on its own (the renderer's background with no sky) is defined once in it, where it is easy to find and
  change, never on a Core type. The few "nothing" values are `.None` (`GpuTexture.None`, `GpuBuffer.None`).

### Disposables

What hands out something that ends when disposed (an event or file watch, an ECS observer, a scheduled system, a
registration, an attachment) returns `Subscription`, not `IDisposable`, and whoever keeps it holds a `Subscription`,
so reading the code says what the thing is and why it is kept. Several kept for as long as their owner go in a
`SubscriptionList`, which one `Dispose` ends, rather than a loop over them.

## Correctness

### Null

- The null-forgiving `!` (`value!`, `null!`, `default!`) is not allowed; the build fails on it (`NX0001`-`NX0003`,
  from Nullable.Extended.Analyzer), and on every nullable warning. No `// !` comment to excuse one either.
- What is always there is not declared nullable: pass it in, make it `required`, or group what comes and goes
  together into one optional object.
- What may be missing is handled where it is read: a guard clause, `is not { } value`, `??` with a fallback or a
  `throw` that says what was expected. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a
  check proves (`Result<T>.Ok`).
- A guard that throws uses the helper that names the check, rather than an `if` and a `throw` written out:
  `ArgumentNullException.ThrowIfNull(x)` and its `ThrowIf*` siblings (`ThrowIfNullOrEmpty`, `ThrowIfNegative`) for
  a parameter, `ObjectDisposedException.ThrowIf` for a disposed owner, `string.IsNullOrWhiteSpace` for a string that
  must say something.

### Async

- Every `await` says `.ConfigureAwait(false)`; the build fails without it (`CA2007`). Tests are the exception:
  xUnit runs them on its own context.
- A task is awaited, kept to be awaited later, or let go with `.FireAndForget()`, which logs what it throws. Never
  dropped (`VSTHRD110`, `CS4014` fail the build), never discarded with `_ =`: that hides what it threw and says
  nothing about what was meant.
- Ordinary async and background work is tasks: async methods, `Task.Run`, `.FireAndForget()`. `Threads` is for the
  engine's own threads, and for work that lasts as long as the run.
- What a worker makes for an engine thread is data, never work: the gem keeps what arrived (a `Channel<T>`, or a
  reference written whole) and takes it in at the start of the hook that owns the structure it goes into, so no thread
  runs code on another's behalf and nothing locks a collection two threads read and write. `Threads.InvokeAsync` is
  for a flow that must await a step on an engine thread and what it returns. Nothing blocks waiting on the main or
  render thread.
- Async all the way up: nothing on the main or render thread, or in the frame loop, blocks on a task (`.Result`,
  `.Wait()`, `.GetAwaiter().GetResult()`), since a thread that waits on work queued behind it deadlocks. A
  synchronous API (`IFileSystem.Read`) is a synchronous implementation, not a wait on its async twin. The one wait
  allowed is at a boundary where every thread that could be held up has already ended, with a comment saying so
  (`Engine.Run` collecting the save after both engine threads are gone).
- Task-returning methods end in `Async` (`VSTHRD200`); no `async void` (`VSTHRD100`).

### Failures

- A `Result` for what can fail on content or the machine: I/O, import, compile, readback.
- Broken content is `Debugging.Error` and a visible stand-in (the glowing failure surface, the failure cube), never
  a stopped frame.
- What works, only worse, is `Debugging.Warn`.
- An exception is for a programmer's mistake, and for a gem that cannot exist at all (no GPU device). Its type
  says which mistake: `ArgumentException` and its kin for a bad argument, `InvalidOperationException` for a call at
  the wrong time, `NotSupportedException` for what this implementation does not do; never `Exception` itself, which
  says nothing a catch could act on.
- Nothing is caught and dropped. What is caught is logged (`Debugging.Error`, saying what failed and on what) or
  rethrown with `throw;`, so the cause survives. `catch (Exception)` is for the boundaries that keep one failure
  from stopping the run (a gem's hook, a loader, a task let go), and it logs there; everywhere else a catch names
  the exception it expects or filters with `when`.
- A log line says what happened and to what (the asset, the gem), once. What repeats every frame is a
  `Debugging.Verbose` line or a metric, never an `Info`, `Warn` or `Error` each frame, which buries the line that
  mattered.
- `Debugging.Assert` is for state that cannot be, where going on would be worse than stopping: it ends the process at
  once, leaving a crash file. A caller's mistake at an API stays an exception, which takes only that gem out of the
  loop.

### Performance

Asynchronous where work can wait, and fast in the frame loop. Outside the frame loop the simple form comes first,
and a change for speed follows a measurement, taken in a Debug build, since Debug is the baseline that must never
hitch and so is where a hitch is measured. Inside the loop:

- **No LINQ in the frame loop.** Nothing that runs every frame (`Update`, `Render`, physics, a system's `Run`, a
  render pass) uses LINQ, allocates closures or formats strings: plain loops, delegates made once and kept.
- **Spans for chunks of data.** Pass `Span<T>`/`ReadOnlySpan<T>` instead of slicing arrays onto the heap. A type that
  must stay on the stack is a `ref struct`.
- **`ValueTask` when it is usually done.** A method that completes synchronously most of the time (a cache check)
  returns `ValueTask`/`ValueTask<T>`, not `Task`.
- **Off the main thread where it can be.** Work over many entities is a parallel system; loading and building are
  tasks that hand what they made to the main thread in one batch; drawing is the render thread's.
- **Function pointers for hot native calls.** A native function called often is called through a `delegate*`, not a
  delegate.

## Style

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

### State

- Public and internal state is exposed as `PascalCase` properties, never as fields.
- If a value is set once, use `{ get; }` and assign it in the constructor.
- If a value can change, use `{ get; set; }`, or `{ get; private set; }` when writes need a tighter scope.
- If a value is derived from other state, use an expression-bodied property (`=>`).
- Private state is a private field (`_camelCase`), declared with the other fields at the top of the type, whatever it
  holds: an injected dependency, a lock, a collection, a delegate made once and kept (`_renderFrame = RenderFrame`,
  so a frame allocates no closure), a flag, this frame's values for a callback. `{ get; set; }` is for what other code
  reads; where reading or writing private state has to do something, that is a method.
- The exception is interop and data structs, where layout or the JSON shape depends on fields
  (`[StructLayout]`, GPU structs, ECS components). They keep their public fields.

### Methods

A method should read from top to bottom:

1. Guard clauses and validation first, returning or throwing early.
2. Setup next, using `using` declarations rather than nested `using` blocks.
3. The main path, kept at one level of indentation where possible. Prefer early `continue` and `return` to
   nested `if`/`else`.
4. The result.
5. Local functions last, after the final `return`.

Separate these steps with a blank line.

A method is one step of its caller's story, small enough to take in at a glance. A long method that is a sequence
of steps becomes those steps as methods, called in order from the original, so the original reads as its outline;
each step gets a summary saying why, where that is not obvious. A table-shaped method (one `switch` over many cases)
stays whole. A loop body nested three deep or more is an early `continue`, or a method named for the step.

An `if` whose body is one short statement is one line, with no braces: `if (count == 0) return;`,
`if (!TryGetResource(name, out resource)) continue;`. Anything longer, an `else`, and every loop (`for`, `foreach`,
`while`) has braces on their own lines, however short the body, so that what repeats always reads as a block. The
build requires braces around any multi-line body (`IDE0011`).

### Naming

- Read a name as `Class.Member(arguments)`. If that says what happens or what is held, the name is good, however
  short: `Assets.Find<Texture>(path)`, `Caches.ReadAsync<GpuShader>(key, version)`, `Lifetime.Stop(reason)`. If it
  does not (a `Pump()` on a system, a `_scratch` buffer), name the object: what is pumped, what the buffer holds.
- A method's prefix says what kind of thing it does. `Get` returns what the object holds or looks up and never
  fails (`ClusterPool.GetRow(model)`, `Assets.GetPath(handle)`); `Is`, `Has` or `Can` returns a bool that answers a
  question (`IsDrawable`, `HasPass`, `CanSee`); `Try` returns a bool that says whether it worked, with the result
  in an `out` parameter, or a `Result` (`TryGetResource(scope, name, out resource)`, `TryRecordDraw(pass)`); and
  the two combine as `TryGet` for a lookup that can fail. Other verbs say what else happens: `Make`, `Parse`,
  `Format`, `Find` (which may find nothing, and says so with a `?`), `To` for a conversion. A method returning a
  value is never a bare noun (`Amount`, `Shown`, `RowOf`): the verb is what tells the reader whether it computes,
  looks up, converts or can fail. A static factory (`Result.Success`, `Tag.Of`), a fluent builder step
  (`render.Viewport(area).Pipeline(pipeline)`), an immediate-mode widget whose bool means "used this frame"
  (`IDebugUI.Button`) and a collection's `Add`/`Remove` keep .NET's own idioms.
- A reusable buffer is named for its one purpose, and has only one.
- No single-letter locals or lambda parameters, except loop counters, `x`/`y`/`z`, and the coefficients of a
  published formula. Established short words (`ctx`, `cls`, `sb`, `dt`, `ex`) stay.
- A local does not change meaning within a method, and does not shadow a type.
- Every method that returns a `Task` or `ValueTask` ends in `Async`, private helpers included
  (`LoadAsync`, `GenerateAsync`, `LoadFileAsync`). A method that waits for the same work on the calling thread has
  the name without it (`IFileSystem.Read` beside `ReadAsync`). Test methods are the exception: their name is the
  sentence they check.
- An async method takes a `CancellationToken cancel` as its last parameter and passes it to everything it awaits,
  unless nothing in it could stop. Long work whose share done can be measured (opening a domain, generating a model's
  clusters) also takes an `IProgress<float>? progress`, 0 to 1, just before the token; a single step (one file read,
  a hand-off to a thread, a compile) does not. Both are optional (`= null`, `= default`) on a service's public methods
  and required on interfaces and private helpers.

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

## Shaders

- Shaders are HLSL only, compiled by whichever gem provides `IShaderCompiler`, which loads their `#include`s from
  the assets: HLSL 2021, Shader Model 6.0 with wave intrinsics, warnings are errors.
- Resources bind through `Include/Bindings.hlsli`'s macros (`READ`, `SAMPLER`, `WRITE`, `UNIFORM`), never a register
  by hand. Its header holds the conventions every shader is written against: frame, winding, reverse-Z, matrices,
  physical light units and pre-exposure, colour.
- Wave intrinsics only where the result cannot depend on the wave's size, 32 or 64 by GPU, or where it hands out the
  places of a count the atomics' order already made arbitrary (`WaveAllocate`). No 16-bit types and no runtime-indexed
  resource arrays.
- A value that can go wrong is kept from it where it arises: roughness clamped (`RoughnessToAlpha`), n·v kept above 0,
  `NormalizeOrZero` where a vector can be zero, explicit gradients or levels (`SampleGrad`, `SampleLevel`) inside a
  branch.
- Few variants: a define for what changes the pipeline (which surface, forward or not), a `[branch]` on a value the
  whole draw shares for what does not.
- One copy of each piece: the BRDF, the lights and their units, and the shading models live once, in the shared
  shader library ([Shaders](src/Bundled/Resources/Shaders/README.md)), and every renderer shades with them.
- A technique from a paper is written here from the paper, and its comment names the paper; no third-party shader
  code is copied in.

## Cross platform

Magic targets Windows, Linux and macOS from one code base. Nothing below is optional on the grounds that "it works
on my machine": the other machines are the point.

### Paths

- Never write a separator, split on one, or compare path prefixes as strings. Build a path with
  `IFileSystem.Combine`, and take one apart with `IFileSystem.GetParent`. `FileSystem` is the one place that knows what a
  separator is.
- `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine: they know every OS's rules.
- No drive letters, no absolute paths, no checking a string for `..`.
- **An asset path is not an OS path.** It is relative to its asset root and always uses `/`, on every OS.
  Core's `Assets` converts at its boundary; nothing else converts one into the other.
- Linux file systems are case-sensitive. A file name in code, an asset, a shader `#include`, a csproj or a `.meta`
  matches the file on disk exactly. The engine compares paths ignoring case so that every OS behaves the same, which
  means two files may never differ by case alone.

### Disk

- All disk access goes through `IFileSystem`, never `File` or `Directory`.
- The one exception is `Debugging`, the log: it writes crash files itself, so that it works before any service
  exists and after one has broken.
- Engine output goes beside Magic, a folder per kind (`Logs`, `Cache`, `Screenshots`), never a hardcoded temp, home
  or AppData path.
- File watch events differ per OS: duplicated, coalesced, out of order. Treat one as "something changed here",
  collect them in one place, and check what exists.

### Text

- Files and generated shader source are written with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM.
  Readers accept `\r\n` and a BOM.
- Numbers and dates are parsed and formatted culture-invariantly.

### Platform code

- No OS APIs in Core: no P/Invoke, no registry, no `.exe`/`.so`/`.dylib` names, no `OperatingSystem.Is*`.
  Anything platform-specific lives in a gem, behind one of Core's interfaces (`IWindow`, `IInput`, `IGpu`),
  guarded by `OperatingSystem.IsWindows()` and its siblings.
- A native library is referenced by its logical name (`"name"`, never `"name.dll"`). Its binary comes with the gem
  that declares it, built from the library's own source, under `runtimes/<rid>/native/`, where the gem's load
  context finds it, and one gem declares each library so every gem that binds the name gets the same copy
  ([Gems](src/Core/README.md#gems)).
- No csproj names a runtime identifier: a gem carries the native binaries built on the machine that built it, which
  is the one it runs on.
- Repository scripts are one cross-platform script each, PowerShell that runs under `pwsh` and Windows PowerShell
  5.1, or a `dotnet` command. No `.bat`/`.sh` pairs ([Tools](tools/README.md)).

### Graphics

- Drawing goes through `IGpu`: a gem makes GPU objects and records a frame's commands as plain data
  (`GpuCommands`), which the GPU provider turns into its API's calls. Backend-specific code lives only in that gem.
- What is drawn into is a render target, a `GpuTexture`: a render texture, or a window's (`IGpu.GetWindowTarget`).
  Drawing code takes a target and asks its format and size; it never assumes a window.
- A GPU feature is used only if Vulkan, D3D12 and Metal all have it.

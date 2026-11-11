# Code Standards

The rules for C# and build files in Magic; [Shader Standards](SHADER_STANDARDS.md) has Slang's. `.editorconfig` and
`BannedSymbols.txt` enforce what a tool can check; this page covers the rest. Every warning except a TODO comment's
fails the build. New code follows these rules, and old code catches up when you touch it.

## How we write code

Write the engine as data flowing through functions, not objects calling each other. Everything is one of four things:

- **Data:** plain values, such as `Domain` and `Prefab`.
- **Databases:** one place that holds state and does little else, such as the assets' database and the ECS's columns.
- **Services:** what you call, such as `world.Create` and `assets.Find`.
- **Steps:** work done in an order, as named methods that take values and return values. `Program` and
  `AssetManager.RunImportAsync` read this way.

- **Nothing glued in.** Just about everything is a gem that can be swapped out.
- **Direct calls, not webs.** Data moves A â†’ B â†’ C, and the whole flow reads top to bottom in one place. Call a thing by
  its name, so go-to-definition and a stack trace lead to it. No events firing into handlers that fire more events.
- **Stateless by default.** A function takes what it needs and returns a result.
- **Testable.** Each piece is modular, can be tested on its own, and is easy to debug.
- **Functions over objects.** Records, plain data and static functions. No objects that guard their own fields.
- **Composition over inheritance.** When using inheritance, keep it shallow.
- **Async for anything slow.** Work that can take longer than a frame is an `Async` method.
- **Shallow.** Flat methods, flat folders, short call chains.
- **Simple and small.** No abstraction before a second real use, and no guessing at edge cases.

## Ground rules

- When two places do the same thing, one function does it. No helper with a single caller.
- Delete dead code instead of commenting it out. No unused private or internal member, parameter or overload. Public
  API a gem or game could use stays without a caller.
- Use a known pattern only for the problem it solves, and name it in a comment (a pool, a fold).
- These rules come first, then .NET's Framework Design Guidelines. Use current C# and .NET, and one word for one thing
  across the code base.
- Managed dependencies come from NuGet. A package with a known vulnerability, direct or transitive, fails the restore.
- Native libraries are built from source or downloaded as their author publishes them. No third-party wrapper, package,
  binary or copied code. Hand-write a binding only where the generator can't, and say so in the gem's README.
- No keys, tokens or passwords in code, configuration or workflows. CI reads its own from the repository's secrets.

## Gems, hooks and services

- A gem is a plugin: a decoupled, hot-reloadable, modular piece of logic that a project adds or replaces to meet its
  needs. It is a `public sealed` class implementing `IGem` and the Core interfaces it provides.
- A gem's constructor takes everything it depends on and starts what the gem is; `Dispose` ends it. Only code that
  builds objects from constructors takes `IServiceProvider`. Gems are made in dependency order, then in the order the
  project lists them, and disposed in reverse.
- A hook is what the engine calls on a gem each frame: `IGem`'s `Update`, `FixedUpdate` and `LateUpdate`, in that order
  over the gems, on the game thread, then the `Render` of each gem that renders, on the render thread, which owns the
  GPU. The platform thread, the process's first, pumps the OS's window and events through
  the gem that implements `IPlatform`, which hands the game thread the input they make as data.
  `Render` records into the frame's `RenderGraph`, each pass in its `RenderStage`, and Core hands it to the GPU provider
  once every gem has. A gem renders through the form that fits it: `IRender` takes and makes nothing for other gems,
  `IRenderFrom<TInput>` takes their output, `IRenderInto<TOutput>` makes its own, and `IRender<TInput, TOutput>` does
  both. An input is a record of other gems' outputs, and an output one type or a tuple of types, each made for the gems
  that take it. Core runs each gem after the gems whose output it takes; gems pass what they draw from one another
  that way, as data, never through a gem in a constructor.
  `LateUpdate` hands `Render` what it draws as data, the newest through a `Mailbox<T>` and what must not be lost
  through a `Channel<T>`. `Render` reads a gem's settings record directly: it is immutable and written whole. A gem implements the ones it needs. One that throws in a hook is logged
  and leaves every hook.
- Where Core needs something from a gem, the gem implements an interface and Core calls it, as with `IEcs` and
  `IAssetImporter<T>`. Add one when a gem first needs it.
- A gem that keeps something for each entity compares what it holds with the entities each frame, adding what came and
  dropping what went, as the renderer does. One that must also drop what it built up when the world is replaced, or ready
  what comes before it shows, implements `IHookIntoDomainLoading`; the world awaits it while the loading screen shows.
- `Program` sets a run up and owns its loop, calling each service's `Pump` in turn. `App` lasts the whole process; what a
  run makes is new for it, and a restart keeps only the world, as JSON, and the gems' settings. A service starts in its constructor
  and ends in `Dispose`, not with paired start and stop calls. A step is a method. A class of its own is for a substantial piece of work.
- A change is a call on `World`.
- A method that needs the game thread after an await of its own steps onto it with
  `await threads.Enter(EngineThread.Game)`; nothing hops threads for it.
- Gems talk through message structs, each an `IEvent`. Publish one from any thread and every gem reads it the next
  frame, from `frame.Events`, in load order. A gem that sets `IsHandled` on one takes it from the gems after it.
- Async work a hook starts arrives in a later frame: by a message, or by work posted with `Threads.Post`. A gem that
  needs slow setup starts it in its constructor and reads what came of it in a hook.
- `Dispose` frees what the gem started, once none of its work still runs on a worker.
- A gem's csproj states its `Description`, `Version` and `Authors`. Nothing else does.
- A value a player, game or developer would tune is a member of its gem's settings record, never a const, a
  `#if DEBUG` choice, a key-only toggle or a console string. The gem implements `IHasSettings<TSettings>`, and the
  record is a `sealed record` of init properties with a `[Category]`, each with its default and a doc comment.
  Game code reads and sets it through `Settings` by type; the console and tools by key, as `Rendering.Resolution`.
- Gems may depend on each other and reference one another. A gem throws when a dependency it needs isn't there.
- A component is an unmanaged struct with public fields that implements `IComponent`, and members for what follows from
  those fields.
- Work over many entities is a system: a function over chunks a gem calls from a hook, through `IEcs.Query` to read or `IEcs.Mutate` to write, on its own
  thread.
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
- Every `await` has `.ConfigureAwait(false)`, except `await threads.Enter(...)`, which has none, and in tests.
- Await every task, keep it to check later, or call `.FireAndForget()`. Never drop it or discard it with `_ =`. No
  `async void`.
- Cancellation ends the task with `OperationCanceledException`, never a failed `Result`.
- Nothing blocks the game or render thread: no `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`, and no waiting on
  the other engine thread. Two waits are allowed, each with a comment saying so: at a boundary where every thread it
  could hold up has ended, and Core's game thread waiting at the end of a frame until the render thread has drawn the
  frame before. The render thread never waits for the game thread. The platform thread waits on the OS for its next
  event.
- A worker hands an engine thread data (a message, a `Channel<T>`, a value written whole) or work
  (`Threads.Post`). No locks on collections two threads share.
- Background work is a task. `Threads` is for the engine's own threads.
- A timeout is the caller's, through a linked token source.

## Failures and null

- Return a `Result` for anything content or the machine can break: I/O, import, compile, readback.
- Broken content logs `Log.Error` and shows a visible stand-in; it never stops the frame. What still works, only
  worse, logs `Log.Warn`.
- Throw only for a programmer's mistake or a gem that can't exist (no GPU): `ArgumentException`,
  `InvalidOperationException` or `NotSupportedException`, never `Exception` itself. Use the throw helpers
  (`ArgumentNullException.ThrowIfNull`, `ObjectDisposedException.ThrowIf` and their siblings).
- Never catch and drop. Log what you catch with what failed and on what, or `throw;`. Catch `Exception` only where one
  failure mustn't stop the run (a gem's hook, an importer, a forgotten task); elsewhere catch what you expect or
  filter with `when`.
- A log line says what happened and to what, once. Anything that repeats every frame is `Log.Verbose` or a metric.
- `Assert` is for impossible state and ends the process with a crash file.
- No null-forgiving `!` (MG0002); it and every nullable warning fail the build. Don't make what is always there nullable: pass it
  in, make it `required`, or group the optional parts into one object.
- Handle a missing value where you read it, with a guard clause, `is not { } value`, `??` or a `throw` saying what's
  missing. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a check proves.

## Performance

- Outside the frame loop, write the simple version first and optimise only after measuring in a Debug build.
- Nothing that runs every frame uses LINQ, closures or string formatting. Use plain loops and delegates made once.
- Pass `Span<T>` and `ReadOnlySpan<T>`, not array slices. A type that must stay on the stack is a `ref struct`.
- Return `ValueTask` from a method that usually finishes synchronously.
- Call frequent native functions through function pointers (`delegate*`).

## Structure

### Visibility

- Public is only for what a gem or game script uses. Everything else is `internal` or private. Container and host
  plumbing are never public, and nothing is public for a test's sake.
- A type only one gem uses lives in that gem, internal. Core holds the types gems share.
- Add an interface only to swap a gem or to reach outside the process (disk, GPU, window), never for a test.
- No type that only forwards calls to another.

### Folders and files

- A namespace is the project's root namespace (`Magic` for Core, `Magic.<Name>` otherwise) plus its folders.
- Core is one project, `src/Core/Magic.Core.csproj`. Its root holds only what starts the process; every other type
  goes in the folder of its area, such as `Files` or `Plugins`. A new area is a new folder, never a new project.
- A folder exists only once it holds something. Split a big one into a subfolder per area, never named like a type in
  it.
- A gem is flat: every file at its root, in `Magic.<Gem>`. Its `Resources/` holds the assets and scripts only it uses.
- Built-in assets every project can use go in `src/Resources/`.
- Name a file for its main type. Small types that belong with it may share the file.
- Extension members are for a type the project doesn't own, or an interface; anything else is a member of its type.
  They go in `extension(T x) { }` blocks in an `XExtensions` class. Related types share one class.

### Assets

- An asset type holds what its users read: plain data, properties only, no JSON attributes, no file details, no I/O.
  Separate code reads it.
- An asset type gems share is Core's. One only a gem reads is that gem's.
- Reading a file format is its importer's one job, and writing one its exporter's; anything else known about an asset,
  its sidecar included, is Core's.
- What a gem makes from an asset, such as a renderer's clusters of a model, is an `IAssetPreprocessor<T, TMade>` the gem
  registers with `AssetManager`: it runs as the asset loads, and what it made is in `Caches` under the asset's id and
  version for the gem to read. `bake` runs every one on every asset.
- An import setting is a property of the asset type, declared as one, never a settings type beside it.
- A key the file must have is `required { get; init; }`. One with a default is `{ get; set; }`, the default as its
  initializer and nowhere else. What code sets, not the file, has no public setter.
- Read and write all JSON with Core's one dialect. No per-type options.
- Every asset type has a default asset, found by convention, never by a path in code or settings.
- Refer to an asset by its typed handle, never a path, except in code that starts from a name.
- A "nothing" value is a static `None` on its type. An implementation's own fallback lives once in it, never on a Core
  type.

## Style

CSharpier decides layout, every line break, indent and blank line, from `.editorconfig`'s `[*]` block: the editor's
extension prints a file on save, `tools/Scripts/Format.ps1` the files a change touches
([Format.ps1](../tools/README.md#formatps1)), a local build what is left, and `Validate.ps1` refuses a file it would
change. The rules below that no
built-in rule checks are [tools/Analyzers](../tools/Analyzers/README.md).

### Type layout

- A type's members go by kind, fields first and nested types last; within a kind public before private; constants
  before other fields; static before instance; `Dispose` before the other methods. MG0001 checks it and
  `Format.ps1` puts members there; the exact kind order is its code.

### Methods

- A method reads top to bottom: guard clauses, setup with `using` declarations, the main path kept flat with early
  `continue` and `return`, the result, then local functions.
- A method may be long when it is one flow. Split it into sections under short comments, not into methods with one
  caller; a step becomes a method of its own when a second caller needs it. A method that is one big `switch` stays
  whole.
- Nothing nests three deep: use a guard clause, an early `continue`, or a method named for the step.
- Two values: `?:`. More: a switch expression or early returns. Never a `?:` inside a `?:`.
- A one-statement body uses `=>`, which ends its line when the body wraps. A primary constructor only keeps its
  parameters; one that does work is explicit.
- One-statement `if` and `else` bodies have no braces and go on the line below. Longer bodies, wrapped conditions
  and every loop have braces; each branch decides for itself. A blank line goes before `return`, `continue`, `break`
  or `throw` unless it opens its block.

### API shape

Write plain statements first. Use one of these shapes where it makes the call site shorter and plainer, picked by
what the caller does:

| The caller | Shape | How |
| --- | --- | --- |
| Describes what should exist: settings, a description, an asset | Declarative | Plain data set in an object initializer, `required { get; init; }` for what must be given and `with` for a changed copy. The default. |
| Changes a value in a known order | Steps | Named methods that take a value and return one, called in order and assigned. |
| Says what it wants in one sentence, such as a query or a chain of setup | Fluent | Each method returns its receiver, or a struct's new value, so the chain reads as `subject.Verb(x).Verb(y)`. |
| Builds what is valid only once complete, over many calls or across members | Builder | Calls collect into the builder, and `Build()` checks them and returns the finished, immutable value. Only where an initializer with `required` can't express it. |

- No `Pipe`, `Cascade`, `Then` or other method that only calls the function it is given. Assign each step, and set
  members in an initializer or as statements:

  ```csharp
  state = Integrate(state, stepSec);
  state = ResolveContacts(state);
  ```

- In the frame loop, work over many values is a system that changes chunks in place through spans, never a chain
  that returns new copies ([Performance](#performance)).

### Naming

- A name reads well as `Class.Member(arguments)`: `AssetManager.Find<Texture>(path)`, `App.Exit()`. Action first,
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
- Pipelines and entry points keep general verbs: `Run`, `Start`.
- A method that returns a value is never a bare noun (`Amount`, `RowOf`).
- A number that's a time ends in its unit (`FixedStepSec`, `_settleMs`). A `TimeSpan` doesn't (`_settleTime`).
- No single-letter names except loop counters, `x`/`y`/`z` and formula coefficients. `ctx`, `sb`, `dt` and `ex` are
  fine. A local keeps one meaning and never shadows a type.
- A reusable buffer has one purpose and is named for it.
- Keep .NET's idioms: static factories (`Result.Success`) and the shapes in [API shape](#api-shape).
- Test methods read as the sentence they check.

### Comments

- XML doc comments on types and non-obvious members, saying why, not what. `<summary>` and `</summary>` go on their own
  lines.
- Put a `//` comment above a block whose purpose isn't obvious at a glance (a loop doing two things, a calculation, an
  order that matters, a workaround). It covers the block down to the next blank line. Never restate the line.
- No divider comments and no `#region`.

## Cross platform

All code runs on Windows, Linux and macOS.

- **Paths.** Build them with `IFileSystem.Combine` and split them with `IFileSystem.GetParent`; never touch separators.
  `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are fine. No drive letters, absolute paths or
  checks for `..`. An asset path is relative to its root and uses `/`; only `AssetManager` turns it into an OS path.
- **Case.** File names in code, assets, `#include`s, csproj and `.meta` files match the disk exactly. Two files never
  differ only by case.
- **Disk.** All disk access goes through `IFileSystem`, never `File` or `Directory`; only `Debugging` is exempt. Engine
  output goes next to Magic in a folder per kind (`Logs`, `Screenshots`), never a temp, home or AppData path. The cache,
  and each project's settings and saves under `Magic/<project>` in the user's local application data, are the
  exceptions. Treat a file watch event as "something changed here": collect them in one place and check what exists.
- **Text.** Write files and generated shader source with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM.
  Accept `\r\n` and a BOM when reading. Parse and format numbers and dates culture-invariantly.
- **Platform code.** No OS APIs in Core: no P/Invoke, registry, `.exe`/`.so`/`.dylib` names or `OperatingSystem.Is*`.
  Platform code lives in a gem behind a Core interface, guarded by `OperatingSystem.IsWindows()` and its siblings.
- **Native libraries.** Reference one by its logical name (`"name"`, never `"name.dll"`). One gem declares it and ships
  its binary under `runtimes/<rid>/native/`. No csproj names a runtime identifier.
- **Scripts.** Repository scripts are one PowerShell file each, for `pwsh` and Windows PowerShell 5.1, or a `dotnet`
  command. No `.bat`/`.sh` pairs.
- **Graphics.** Draw through Core's `RenderGraph`: a gem records commands as plain data and the GPU provider turns
  them into API calls; only the GPU provider has backend code. Draw into a render target (a `GpuTexture`), asking its
  format and size; never assume a window. Use a GPU feature only if Vulkan 1.3 has it on every GPU Magic runs on, a Mac's MoltenVK and
  KosmicKrisp included. Anything more is optional, with a path that works without it.

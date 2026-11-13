# Code Standards

The rules for C# and build files in Magic. [Shader Standards](SHADER_STANDARDS.md) has the rules for Slang.
`.editorconfig` and `BannedSymbols.txt` enforce the rules that a tool can check. This page has the other rules. Each
warning, except the warning of a TODO comment, fails the build. New code follows these rules. Old code changes to
follow them when you change it.

## How we write code

Write the engine as data that flows through functions, not as objects that call each other. Everything is one of four
things:

- **Data:** plain values, such as `Domain` and `Prefab`.
- **Databases:** one place that holds state and does little else, such as the assets' database and the ECS's columns.
- **Services:** what you call, such as `world.Create` and `assets.Find`.
- **Steps:** work done in an order, as named methods that take values and return values. `Program` and
  `AssetManager.RunImportAsync` read this way.

- **Nothing glued in.** Almost everything is a gem that a project can replace.
- **Direct calls, not webs.** Data moves from A to B to C, and the whole flow reads from top to bottom in one place.
  Call a thing by its name, so that go-to-definition and a stack trace lead to it. No events that fire into handlers
  that fire more events.
- **Stateless by default.** A function takes what it needs and returns a result.
- **Testable.** Each piece is modular. You can test it on its own, and it is easy to debug.
- **Functions over objects.** Records, plain data and static functions. No objects that guard their own fields.
- **Composition over inheritance.** If you use inheritance, keep it shallow.
- **Async for anything slow.** Work that can take longer than a frame is an `Async` method.
- **Shallow.** Flat methods, flat folders, short call chains.
- **Simple and small.** No abstraction before a second real use, and no guesses about edge cases.

## Ground rules

- When two places do the same thing, one function does it. No helper with a single caller.
- Delete dead code. Do not comment it out. No unused private or internal member, parameter or overload. Keep public
  API that a gem or game can use, also when it has no caller.
- Use a known pattern only for the problem it solves, and name it in a comment (a pool, a fold).
- These rules come first, then .NET's Framework Design Guidelines. Use current C# and .NET. Use one word for one thing
  in all the code base.
- Managed dependencies come from NuGet. A package with a known vulnerability, direct or transitive, fails the restore.
- Build a native library from source, or download it as its author publishes it. No third-party wrapper, package,
  binary or copied code. Write a binding by hand only where the generator cannot, and say so in the gem's docs.
- No keys, tokens or passwords in code, configuration or workflows. CI reads its own from the repository's secrets.

## Gems, hooks and services

- A gem is a plugin: a decoupled, hot-reloadable, modular piece of logic that a project adds or replaces to meet its
  needs. It is a `public sealed class EntryPoint` at the gem's root, and its parts.
- `EntryPoint` implements only `IGem` and `IDisposable`. Its only state is its parts. It makes each part with `new` in
  its constructor and exports it as a public get-only property. A private field or an `internal` class is not
  exported. Its hooks call its parts, and `Dispose` ends them. A gem without hooks still has an `EntryPoint`.
- A part is a class that does one job. Core sets up each exported part by the Core interfaces that the part
  implements. These interfaces say what the part does: it provides them to the gems that are made after it, it
  renders, it reads assets, or it is a window, platform, ECS or log sink. Name a part for its interface: `Renderer`,
  `Ecs`, `Window`, `Input`, `Clipboard`, `Platform`, `GraphicsApi`, `ShaderCompiler`, `DebugUI`, `Importer`,
  `Exporter`. Declare its property as its class, not as an interface. Its constructor is
  `internal`. Parts that share native state take one shared object from `EntryPoint`.
- The constructor of `EntryPoint` takes everything that the gem depends on: Core's services and the parts of other
  gems. Only code that builds objects from constructors takes `IServiceProvider`. Gems are made in the order of their
  dependencies, then in the order that the project lists them. They are disposed in the reverse order.
- A hook is what the engine calls on a gem each frame. `IGem`'s `Update`, `FixedUpdate` and `LateUpdate` run in that
  order over the gems, on the game thread. Then the `Render` of each gem that renders runs on the render thread, which
  owns the GPU.
- The platform thread is the first thread of the process. It pumps the OS's window and events through the part that
  implements `IPlatform`. That part gives the game thread the input from them as data.
- `Render` records into the frame's `RenderGraph`, each pass in its `RenderStage`. When every gem has recorded, Core
  gives the graph to the GPU provider. A gem's `Renderer` renders through the form that fits it:
  - `IRender` takes nothing from other gems and makes nothing for them.
  - `IRenderFrom<TInput>` takes their output.
  - `IRenderInto<TOutput>` makes its own output.
  - `IRender<TInput, TOutput>` does both.
- A `Renderer` with more than one step in the frame implements a form for each step, and Core runs each as its own
  step. An input is a record of the outputs of other gems. An output is one type or a tuple of types, and each type
  is made for the gems that take it.
- Core runs each gem after the gems whose output it takes. Gems pass what they draw to one another in this way, as
  data. They never pass it through a gem in a constructor.
- `EntryPoint.LateUpdate` calls a method of `Renderer` that publishes what `Render` draws, as data. `Renderer` owns
  the hand-off. The newest value goes through a `Mailbox<T>`, and what must not be lost goes through a `Channel<T>`.
- `Render` reads the gem's settings directly. Core writes them one member at a time, so a frame can see one old
  member and one new member.
- A gem implements the hooks that it needs. If a gem throws in `Update`, `FixedUpdate` or `LateUpdate`, Core logs it,
  and the gem leaves those hooks. If a gem throws in `Render`, that is a failure in the picture, and Core runs it
  again after a pause.
- Where Core needs something from a gem, a part implements an interface and Core calls it, as with `IEcs` and
  `IAssetImporter<T>`. Add one when a gem first needs it.
- A gem that keeps something for each entity compares what it holds with the entities each frame. It adds what came
  and drops what went, as the renderer does. A gem can also need to drop what it built up when the world is replaced,
  or make ready what comes before the world shows. Such a gem has a part that implements `IHookIntoDomainLoading`. The
  world awaits that part while the loading screen shows.
- `Program` sets up a run and owns its loop, which calls the `Pump` of each service in turn. `App` lasts for the whole
  process. What a run makes is new for that run. A restart keeps only the world, as JSON, and the settings of the gems.
- A service starts in its constructor and ends in `Dispose`, not with paired start and stop calls. A step is a method.
  A class of its own is for a large piece of work.
- A change is a call on `World`.
- A method that needs the game thread after an await of its own steps onto it with
  `await threads.Enter(EngineThread.Game)`. Nothing else moves it to that thread.
- Gems talk through message structs. Each message is an `IEvent`. Publish one from any thread, and every gem reads it
  in the next frame from `frame.Events`, in load order. If a gem sets `IsHandled` on one, the gems after it do not get
  it.
- Async work that a hook starts arrives in a later frame, by a message. A gem that needs slow setup starts it in its
  constructor and reads the result in a hook.
- `Dispose` frees what the gem started, when none of its work still runs on a worker.
- A gem's csproj states its `Description`, `Version` and `Authors`. Nothing else does.
- A value that a player, game or developer would tune is a member of its gem's settings. It is never a const, a
  `#if DEBUG` choice, a toggle on a key or a console string. The settings are a `sealed class` that implements
  `ISettings`, with settable properties, a `[Category]`, and a default and a doc comment for each property.
  `EntryPoint` takes the settings in its constructor, exports them as a public property, and gives them to the parts
  that read them. Core changes a member on that same object. Game code reads and sets the settings through `Settings`,
  by type. The console and tools use the key, as `Rendering.Resolution`.
- Gems can depend on each other and reference one another. A gem throws when a dependency that it needs is not there.
- A component is an unmanaged struct with public fields that implements `IComponent`, and members for what follows from
  those fields.
- Work over many entities is a system: a function over chunks that a gem calls from a hook, on its own thread. It
  reads through `IEcs.Query` and writes through `IEcs.Mutate`.
- A game is its assets and scripts, never a gem.

## Async

- Anything that can take longer than a frame has this shape, with no defaults:

  ```csharp
  Task<Result<T>> NameAsync(/* inputs */, IProgress<LoadProgress>? progress, CancellationToken cancel);
  ```

  Drop `progress` when there is nothing to count, and `cancel` when nothing in it can stop. You can report progress
  from any thread. Never report less than the last time for the same work.

- A method that returns `Task` or `ValueTask` ends in `Async`, also a private method. No other method ends in `Async`.
  Its synchronous twin drops the suffix and is written synchronously, never as a wait on the async one.
- Every `await` has `.ConfigureAwait(false)`. The exceptions are `await threads.Enter(...)`, which has none, and the
  awaits in tests.
- Await every task, keep it to check later, or call `.FireAndForget()`. Never drop it or discard it with `_ =`. No
  `async void`.
- Cancellation ends the task with `OperationCanceledException`, never a failed `Result`.
- Nothing blocks the game or render thread: no `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`, and no wait on
  the other engine thread. Two waits are permitted, each with a comment that says so:
  - A wait at a boundary where every thread that it can hold up has ended.
  - Core's game thread waits at the end of a frame until the render thread has drawn the frame before.

  The render thread never waits for the game thread. The platform thread waits on the OS for its next event.
- A worker gives an engine thread data: a message, a `Channel<T>` or a value written whole. No locks on collections
  that two threads share.
- Background work is a task. `Threads` is for the engine's own threads.
- A timeout is the caller's, through a linked token source.

## Failures and null

- Return a `Result` for anything that content or the machine can break: I/O, import, compile, readback.
- Broken content logs `Log.Error` and shows a visible stand-in. It never stops the frame. What still works, but
  worse, logs `Log.Warn`.
- A step of `Render` that can't draw tells `RenderGraph.Fail`, returns for this frame and tries again in the next. It
  never logs the failure itself and never stops for the run ([Failures](DEBUGGING.md#report-a-failure-from-a-gem)).
- Throw only for a programmer's mistake or a gem that can't exist (no GPU): `ArgumentException`,
  `InvalidOperationException` or `NotSupportedException`, never `Exception` itself. Use the throw helpers
  (`ArgumentNullException.ThrowIfNull`, `ObjectDisposedException.ThrowIf` and their siblings).
- Never catch and drop. Log what you catch with what failed and on what, or `throw;`. Catch `Exception` only where one
  failure must not stop the run (a gem's hook, an importer, a forgotten task). In other places, catch what you expect
  or filter with `when`.
- A log line says what happened and to what, once. Anything that repeats every frame is `Log.Verbose` or a metric.
- `Assert` is for impossible state and ends the process with a crash file.
- No null-forgiving `!` (MG0002). It and every nullable warning fail the build. Do not make a value nullable when it is
  always there: pass it in, make it `required`, or group the optional parts into one object.
- Handle a missing value where you read it, with a guard clause, `is not { } value`, `??` or a `throw` that says what
  is missing. `[MemberNotNullWhen]` and `[MaybeNullWhen]` tell the compiler what a check proves.

## Performance

- Outside the frame loop, write the simple version first. Optimise only after you measure in a Debug build.
- Nothing that runs every frame uses LINQ, closures or string formatting. Use plain loops and delegates made once.
- Pass `Span<T>` and `ReadOnlySpan<T>`, not array slices. A type that must stay on the stack is a `ref struct`.
- Return `ValueTask` from a method that usually finishes synchronously.
- Call frequent native functions through function pointers (`delegate*`).

## Structure

### Visibility

- Public is only for what a gem or game script uses. Everything else is `internal` or private. Container and host
  plumbing are never public, and nothing is public for a test's sake.
- A type that only one gem uses lives in that gem, internal. Core holds the types that gems share.
- Add an interface only to swap a gem or to reach outside the process (disk, GPU, window), never for a test.
- No type that only forwards calls to another.

### Folders and files

- A namespace is the project's root namespace (`Magic` for Core, `Magic.<Name>` otherwise) plus its folders.
- Core is one project, `src/Core/Magic.Core.csproj`. Its root holds only what starts the process. Every other type
  goes in the folder of its area, such as `Files` or `Plugins`. A new area is a new folder, never a new project.
- A folder exists only when it holds something. Split a big folder into a subfolder for each area. Never name a
  subfolder like a type in it.
- A gem's files are in `Magic.<Gem>`, and the files in a folder of it in `Magic.<Gem>.<Folder>`. Its `Resources/`
  holds the assets and scripts that only it uses.
- Built-in assets that every project can use go in `src/Resources/`.
- Name a file for its main type. Small types that belong with it can share the file.
- Extension members are for a type that the project doesn't own, or for an interface. Anything else is a member of its
  type. They go in `extension(T x) { }` blocks in an `XExtensions` class. Related types share one class.

### Assets

- An asset type holds what its users read: plain data, properties only, no JSON attributes, no file details, no I/O.
  Separate code reads it.
- An asset type that gems share is Core's. One that only a gem reads is that gem's.
- To read a file format is the one job of its importer, and to write one is the one job of its exporter. Anything else
  known about an asset, also its sidecar, is Core's.
- What a gem makes from an asset, such as a renderer's clusters of a model, is an `IAssetPreprocessor<T, TMade>` that
  the gem registers with `AssetManager`. It runs when the asset loads. What it made is in `Caches`, under the asset's
  id and version, for the gem to read. `bake` runs every one on every asset.
- An import setting is a property of the asset type, declared as one, never a settings type beside it.
- A key that the file must have is `required { get; init; }`. One with a default is `{ get; set; }`, with the default
  as its initializer and nowhere else. What code sets, not the file, has no public setter.
- Read and write all JSON with Core's one dialect. No options for each type.
- Every asset type has a default asset, found by convention, never by a path in code or settings.
- Refer to an asset by its typed handle, never a path, except in code that starts from a name.
- A "nothing" value is a static `None` on its type. The fallback of an implementation lives once in that
  implementation, never on a Core type.

## Style

CSharpier decides the layout from the `[*]` block of `.editorconfig`: each line break, indent and blank line.

- The editor's extension prints a file when you save it.
- `tools/Scripts/Format.ps1` prints the files that a change touches ([Format.ps1](TOOLS.md#formatps1)).
- A local build prints what is left.
- `Validate.ps1` refuses a file that CSharpier would change.

[tools/Analyzers](TOOLS.md#analyzers) checks the rules below that no built-in rule checks.

### Type layout

- The members of a type go in order of their kind, with fields first and nested types last. MG0001 checks the order
  and `Format.ps1` puts members in it. The code of MG0001 has the exact order of the kinds. Also:
  - In a kind, public members go before private members.
  - Constants go before other fields.
  - Static members go before instance members.
  - `Dispose` goes before the other methods.

### Methods

- A method reads from top to bottom: guard clauses, setup with `using` declarations, the main path kept flat with
  early `continue` and `return`, the result, then local functions.
- A method can be long when it is one flow. Split it into sections under short comments, not into methods with one
  caller. A step becomes a method of its own when a second caller needs it. A method that is one big `switch` stays
  whole.
- Nothing nests three deep: use a guard clause, an early `continue`, or a method named for the step.
- Two values: `?:`. More: a switch expression or early returns. Never a `?:` inside a `?:`.
- A one-statement body uses `=>`, which ends its line when the body wraps. A primary constructor only keeps its
  parameters. A constructor that does work is explicit.
- One-statement `if` and `else` bodies have no braces and go on the line below. Longer bodies, wrapped conditions
  and every loop have braces. Each branch decides for itself. A blank line goes before `return`, `continue`, `break`
  or `throw`, unless it opens its block.

### API shape

Write plain statements first. Use one of these shapes where it makes the call site shorter and plainer. Pick the shape
by what the caller does:

| The caller | Shape | How |
| --- | --- | --- |
| Describes what must exist: settings, a description, an asset | Declarative | Plain data set in an object initializer, `required { get; init; }` for what must be given and `with` for a changed copy. The default. |
| Changes a value in a known order | Steps | Named methods that take a value and return one, called in order and assigned. |
| Says what it wants in one sentence, such as a query or a chain of setup | Fluent | Each method returns its receiver, or a struct's new value, so the chain reads as `subject.Verb(x).Verb(y)`. |
| Builds what is valid only once complete, over many calls or across members | Builder | Calls collect into the builder, and `Build()` checks them and returns the finished, immutable value. Only where an initializer with `required` can't express it. |

- A description whose few values are always given takes them in a constructor, as `new("Shadow mask", bytes)`. An
  initializer sets the rest: `new("Fog", width, height, format) { Depth = slices }`.
- No `Pipe`, `Cascade`, `Then` or other method that only calls the function it is given. Assign each step, and set
  members in an initializer or as statements:

  ```csharp
  state = Integrate(state, stepSec);
  state = ResolveContacts(state);
  ```

- In the frame loop, work over many values is a system that changes chunks in place through spans, never a chain
  that returns new copies ([Performance](#performance)).

### Naming

- A name reads well as `Class.Member(arguments)`: `AssetManager.Find<Texture>(path)`, `App.Exit()`. Put the action
  first and the subject second (`ReadDomain`). Name what it acts on, also if the name gets longer. If a name doesn't
  make sense without its summary (`Process()`, `_scratch`), rename it.
- Each verb means one thing:
  - `Get` never fails, but can return null when there is nothing.
  - `Find` searches and can find nothing.
  - `Try` returns whether it worked, with the result in an `out` or a `Result`. `TryGet` is a lookup that can fail.
  - `Create` brings something new into being, never `Make`. `To` converts. `Parse` and `Format` do what they say.
  - `Register` gives something to a service and returns the `Subscription` that takes it back. `Add` and `Remove`
    belong to collections.
  - `Is`, `Has` and `Can` return a bool.
- Pipelines and entry points keep general verbs: `Run`, `Start`.
- A method that returns a value is never a bare noun (`Amount`, `RowOf`).
- A number that is a time ends in its unit (`FixedStepSec`, `_settleMs`). A `TimeSpan` doesn't (`_settleTime`).
- No single-letter names except loop counters, `x`/`y`/`z` and formula coefficients. `ctx`, `sb`, `dt` and `ex` are
  permitted. A local keeps one meaning and never shadows a type.
- A reusable buffer has one purpose and is named for it.
- Keep .NET's idioms: static factories (`Result.Success`) and the shapes in [API shape](#api-shape).
- Test methods read as the sentence they check.

### Comments

- Put XML doc comments on types and on members that are not obvious. They say why, not what. `<summary>` and
  `</summary>` go on their own lines.
- Put a `//` comment above a block whose purpose isn't obvious at a glance (a loop that does two things, a
  calculation, an order that matters, a workaround). It covers the block down to the next blank line. Never restate
  the line.
- Code that has its shape for a tool, such as a method kept apart so that an analyzer gives no false report, says so
  in a comment that names the tool.
- No divider comments and no `#region`.

## Cross platform

All code runs on Windows, Linux and macOS.

- **Paths.** Build them with `IFileSystem.Combine` and split them with `IFileSystem.GetParent`. Never touch
  separators. `Path.GetExtension`, `Path.GetFileName*` and `Path.ChangeExtension` are permitted. No drive letters,
  absolute paths or checks for `..`. An asset path is relative to its root and uses `/`. Only `AssetManager` turns it
  into an OS path.
- **Case.** File names in code, assets, `#include`s, csproj and `.meta` files match the disk exactly. Two files never
  differ only by case.
- **Disk.** All disk access goes through `IFileSystem`, never `File` or `Directory`. Only `Debugging` is exempt.
  Engine output goes next to Magic in a folder for each kind (`Logs`, `Screenshots`), never a temp, home or AppData
  path. The exceptions are the cache, and the settings and saves of each project under `Magic/<project>` in the user's
  local application data. Treat a file watch event as "something changed here": collect the events in one place and
  check what exists.
- **Text.** Write files and generated shader source with `\n`, never `Environment.NewLine`, as UTF-8 without a BOM.
  Accept `\r\n` and a BOM when you read. Parse and format numbers and dates culture-invariantly.
- **Platform code.** No OS APIs in Core: no P/Invoke, registry, `.exe`/`.so`/`.dylib` names or `OperatingSystem.Is*`.
  Platform code lives in a gem behind a Core interface, guarded by `OperatingSystem.IsWindows()` and its siblings.
- **Native libraries.** Reference one by its logical name (`"name"`, never `"name.dll"`). One gem declares it and ships
  its binary under `runtimes/<rid>/native/`. No csproj names a runtime identifier.
- **Scripts.** A repository script is one PowerShell file, for `pwsh` and Windows PowerShell 5.1, or a `dotnet`
  command. No `.bat`/`.sh` pairs.
- **Graphics.** Draw through Core's `RenderGraph`. A gem records commands as plain data, and the GPU provider turns
  them into API calls. Only the GPU provider has backend code. Draw into a render target (a `GpuTexture`), and ask
  for its format and size. Never assume a window. Use a GPU feature only if Vulkan 1.3 has it on every GPU that Magic
  runs on, also a Mac's MoltenVK and KosmicKrisp. Anything more is optional, with a path that works without it.

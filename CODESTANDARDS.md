# Toybox CodeStandard

## Core Engineering Policies

- **Scope**: Keep changes isolated and highly reusable.
- **Duplication**: Avoid redundant code patterns without building single-use helper functions.
- **Simplicity**: Prioritize the simplest, most direct solution first. Avoid over-engineering, unnecessary abstractions, or predicting future edge cases. Add complexity only when a specific problem requires it.
- **Housekeeping**: Permanently delete stale definitions instead of leaving commented placeholders.

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

Within a group, public comes before internal, and internal before private. Keep related members next to
each other.

### Visibility

A Core type or member is `internal` unless a gem or a game script needs it: systems, the container and other
host plumbing are never public. The test projects see Core's internals, so nothing is public for their sake; nor
is anything internal for their sake: a member only its own type uses is private, and the tests go through the
exposed API (see `Tests/README.md`).

### State

- Public and internal state is exposed as `PascalCase` properties, never as fields.
- If a value is set once, use `{ get; }` and assign it in the constructor.
- If a value can change, use `{ get; set; }`, or `{ get; private set; }` when writes need a tighter scope.
- If a value is derived from other state, use an expression-bodied property (`=>`).
- Private fields are only for injected dependencies, locks, internal collections, and backing fields that do
  real work such as validation.
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

Extension members use `extension(T x) { }` blocks in an `XExtensions` class, in the project's `Extensions`
folder. Related types (a family of enums converted the same way) share one class, one block each.

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

- All disk access goes through `IFileSystem`, never `File` or `Directory` (a test's own setup excepted).
- The one exception is `Debugging`: it writes crash files itself, so that it works before any service exists and
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
  Anything platform-specific lives in a gem, behind a Core interface, guarded by `OperatingSystem.IsWindows()`
  and its siblings.
- A native library is referenced by its logical name (`"SDL3"`, never `"SDL3.dll"`). Its binaries come from a
  NuGet package per platform, picked by `$(MagicPlatform)` or a condition on `$(MagicRuntime)`.
- No csproj names a runtime identifier. It comes from `Directory.Build.props`: the build machine's own, or
  `-r <rid>`.

## Graphics

- Shaders are HLSL only and go through the render gem's compiler. Backend-specific code lives only in the render
  gem.
- A GPU feature is used only if Vulkan, D3D12 and Metal all have it through SDL GPU.

## Scripts and tools

Prefer one cross-platform script over a script per OS: repo scripts are written in a cross-platform scripting
language such as PowerShell (`pwsh`), or are a `dotnet` command. No `.bat`/`.sh` pairs.

## Tests

Tests pass on every supported OS. Paths are built with `Combine` or `TempFolder`; no Windows-shaped literals
(`C:\`, `\\`) in inputs or expectations.



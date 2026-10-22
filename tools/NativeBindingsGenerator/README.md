# NativeBindingsGenerator

The generator behind every native library Magic binds: `Directory.Build.targets` runs it for each `NativeLibrary`
item while a project builds, and it turns the library's C headers into the C# API the project compiles. How a library
is declared is in [Gems](../../src/Bundled/Gems/README.md#native-libraries); this is how the headers become C#.

## One read per platform

The headers are read with libclang (through CppAst) once for each target Magic runs on: Windows x64, Linux x64 and
macOS arm64. Each read sees the generator's own `Sysroot/` instead of the machine's standard headers, written from
clang's builtin macros, so a parse depends on nothing installed and reads the same on every machine; a header that
needs a standard header the sysroot lacks is a build error naming it, and the header is added there. A library's own
include folders come from its item.

The three reads run at once, each with a parser of its own, after the headers the library's build would generate
(its shims) are made once for all three, and their results are merged in a fixed order, so what is written is what
reading them one after another would write.

Each platform's declarations become C# text, and the three are merged: a declaration must read the same on every
platform that has it, and one only some platforms have is still declared, as C would declare it and fail to link
elsewhere. The differences allowed are the types whose spelling is the OS's: on these 64-bit targets a 64-bit
integer (`uint64_t`, and `size_t` with it) is `unsigned long long` on Windows and `unsigned long` elsewhere, so `ulong`
facing `CULong` is 64 bits everywhere and written `ulong` (`long` facing `CLong`, `long`); a `wchar_t` is two bytes on
Windows and four elsewhere, so a pointer to one is written `void*`, and the caller encodes for the OS it runs on. A
macro whose value differs per platform is skipped and listed, as no one C# constant could be right. Any other
difference, such as a struct laid out differently per platform, fails the build with an error naming the declaration
and the first place the two platforms' text differs: the generator has no rule for it, and one is added before the
library can be bound. No calling convention is written: every target is 64-bit, where each OS has one, so the platform
default is right everywhere, and 32-bit x86 is not a target.

## What C becomes

- A function is a `DllImport` of the library's logical name, blittable end to end: the binding's assembly disables
  runtime marshalling, so a C `bool` is one byte and every call is direct. `const char*` is `byte*`; a string is the
  caller's to encode. Inline, static and variadic functions have no export to call and are skipped and listed.
- A function pointer is a `delegate* unmanaged<…>`, written out at each use, whether the header declares it as a
  pointer or as a function type taken by pointer (`typedef int FooAssertFn(const char*)`, then `FooAssertFn*`); the
  typedef's name survives only in the comment.
- A struct is a `partial struct` with the header's layout: `Sequential`, or `Explicit` at offset 0 for a union, with
  the `Pack` a `#pragma pack` gave it. A fixed array of a primitive is a `fixed` buffer, flattened when it has several
  dimensions; one of anything else is an `[InlineArray]` struct declared beside the field. A struct that is only
  declared, or a typedef to a pointer of one, is an empty struct named by the typedef, so a handle is a typed pointer
  (`Foo.Context*`) at no cost.
- A run of bit fields shares one backing field, and each is a property over its bits, read and written as C does. A
  run whose fields differ in size has a layout MSVC and GCC disagree on, so its struct is emitted opaque (a pointer
  target only) and listed.
- An unnamed union or struct member is a field named `Union` or `Struct`, of a nested type named `UnionType` or
  `StructType`; a second in the same struct is `Union_` of `Union_Type`.
- An enum keeps its values; it is an `int` unless they need more (what a C compiler picks for one that fits differs
  by ABI, so it is decided from the values alone), and `[Flags]` when every value is a bit or bits already named. The
  flags idiom some libraries use, a typedef to an integer named `X` beside an enum `X_` holding its values, types `X`
  as that enum.
- An object-like macro is a `const` when, after the macros it uses are expanded (function-like ones applied, `##`
  pasted), it is a literal, or arithmetic over literals, casts to integer types and constants already made, which it
  takes its type from; a string macro is a `byte*` to its UTF-8 bytes in the assembly's static data, handed to C as
  a C string constant is; one that uses anything else (a function, a pointer) is skipped and listed.
- A C `long` is `CLong`, as its size differs by platform; `char` is `byte`.
- The header's comments become `<summary>` comments.

## Names

A name's owner is the text before its first underscore (`FOO_CreateContext` is `FOO`'s), or a prefix the item lists
when there is none (`foo=Foo` makes `fooContextCreate` `Foo`'s). The owner is a `static partial class`, or, when it is
a struct of the library (`FooBuffer_Append`), that struct, which gets the function as a static method. A type with
an owner is nested in it (`Foo.Context`); one without stands at the namespace; a constant or function without one goes
in the item's `Class`, named for the library unless it says. The rest of the name is PascalCase: a snake_case or
`SCREAMING_SNAKE` name is title-cased word by word (`INIT_VIDEO` → `InitVideo`), and a name without underscores gets a
first capital and keeps the rest as C wrote it (`contextCreate` → `ContextCreate`; `CreateGPUDevice` and `ABI` stay).
An enum's members lose the enum's own name where they carry it (`FooWindowFlags_NoTitleBar` → `NoTitleBar`), else the
prefix they all share, and are title-cased (`FOO_STATUS_OK` → `Ok`, `FOO_SIMDLEVEL_AVX2` → `Avx2`); one that would
start with a digit is led by the enum's last word (`FooKey_0` → `Key0`). A type or member that would be a C# keyword
gets a trailing underscore, as does a function named like the struct it attaches to (a constructor named for its
struct, `FooCmd_FooCmd`, is `FooCmd.FooCmd_`) and a field named like its struct; a parameter that would be a keyword
gets `@`. Two C names that would become one C# name are an error, never silently told apart.

## Running it by hand

The build writes each library's arguments to `<library>.bindings.rsp` in the project's intermediate folder (under
`Build/obj/<Configuration>/`, where its output is under `Build/bin/<Configuration>/`) and runs
`dotnet Build/bin/<Configuration>/Tools/NativeBindingsGenerator/NativeBindingsGenerator.dll @<that file>`, from the
build's own Magic folder where `MagicBin` moves it; the same command reproduces a run. A file whose text is what is
already there is left alone, so a project compiles again only when its API changed, and the build's stamp beside it
records that generation ran. Errors come out in MSBuild's form, naming the header line, so the build that ran it
fails on them; the summary line counts what was made, names the functions skipped, and points at the `.skipped.txt`
beside the generated file that lists every skip. A `.names.txt` beside it lists every C name and the C# name it
became, struct fields (`Foo_Event.type`) and typedefs to primitives (`FooWindowFlags` is `ulong`) included, for
moving code from one to the other.

The tool references nothing of Magic and is exempt from the engine's banned-symbol list, as it reads headers and
writes files.

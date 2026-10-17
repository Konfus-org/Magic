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

Each platform's declarations become C# text, and the three are merged: a declaration must read the same on every
platform that has it, and one only some platforms have is still declared, as C would declare it and fail to link
elsewhere. The differences allowed are the types whose spelling is the OS's: on these 64-bit targets a 64-bit
integer (`uint64_t`, and `size_t` with it) is `unsigned long long` on Windows and `unsigned long` elsewhere, so `ulong`
facing `CULong` is 64 bits everywhere and written `ulong` (`long` facing `CLong`, `long`); a `wchar_t` is two bytes on
Windows and four elsewhere, so a pointer to one is written `void*`, and the caller encodes for the OS it runs on. A macro whose value differs per platform is skipped
and listed, as no one C# constant could be right. Any other difference, such as a struct laid out differently per
platform, is an error until the generator has a rule for it. No calling convention is written: every target is 64-bit, where each
OS has one, so the platform default is right everywhere, and 32-bit x86 is not a target.

## What C becomes

- A function is a `DllImport` of the library's logical name, blittable end to end: the binding's assembly disables
  runtime marshalling, so a C `bool` is one byte and every call is direct. `const char*` is `byte*`; a string is the
  caller's to encode. Inline, static and variadic functions have no export to call and are skipped and listed.
- A struct is a `partial struct` with the header's layout: `Sequential`, or `Explicit` at offset 0 for a union, with
  the `Pack` a `#pragma pack` gave it.
  A fixed array of a primitive is a `fixed` buffer, flattened when it has several dimensions; one of anything else is
  an `[InlineArray]` struct declared beside the field. A struct that is only declared, or a typedef to a pointer of
  one, is an empty struct named by the typedef, so a handle is a typed pointer (`IPL.Context*`) at no cost.
- An enum keeps its values; it is an `int` unless they need more (what a C compiler picks for one that fits differs
  by ABI, so it is decided from the values alone), and `[Flags]` when every value is a bit or bits already named.
- A function pointer is a `delegate* unmanaged<…>`, written out at each use; the typedef's name survives only in the
  comment.
- An object-like macro is a `const` when, after the macros it uses are expanded (function-like ones applied, `##`
  pasted), it is a literal, or arithmetic over literals, casts to integer types and constants already made, which it
  takes its type from; a string macro is a `byte*` to its UTF-8 bytes in the assembly's static data, handed to C as
  a C string constant is; one that uses anything else (a function, a pointer) is skipped and listed. A C `long` is `CLong`, as its size differs
  by platform; `char` is `byte`.
- The header's comments become `<summary>` comments.

- A run of bit fields shares one backing field, and each is a property over its bits, read and written as C does. A
  run whose fields differ in size has a layout MSVC and GCC disagree on, so its struct is emitted opaque (a pointer
  target only) and listed. An unnamed union or struct member is a field named `Union` or `Struct`, of a nested type
  named `UnionType` or `StructType`.
- Dear ImGui's and Vulkan's flags idiom, a typedef to an integer named `X` beside an enum `X_` holding its values,
  types `X` as that enum.

## Names

A name's owner is the text before its first underscore (`SDL_CreateWindow` is `SDL`'s), or a prefix the item lists
when there is none (`ipl=IPL` makes `iplContextCreate` `IPL`'s). The owner is a `static partial class`, or, when it is
a struct of the library (`ImGuiIO_AddKeyEvent`), that struct, which gets the function as a static method. A type with
an owner is nested in it (`SDL.Window`); one without stands at the namespace; a constant or function without one goes
in the item's `Class`, named for the library unless it says (`ImGui`, for Dear ImGui's free functions). The rest of the name is PascalCase: a snake_case or `SCREAMING_SNAKE` name is
title-cased word by word (`INIT_VIDEO` → `InitVideo`), and a name without underscores gets a first capital and keeps
the rest as C wrote it (`contextCreate` → `ContextCreate`; `CreateGPUDevice` and `HRTF` stay). An enum's members
lose the enum's own name where they carry it (`ImGuiWindowFlags_NoTitleBar` → `NoTitleBar`), else the prefix they all
share, and are title-cased (`IPL_STATUS_SUCCESS` → `Success`, `IPL_SIMDLEVEL_AVX2` → `Avx2`); one that would start
with a digit is led by the enum's last word (`ImGuiKey_0` → `Key0`). A name
that would be a C# keyword gets a trailing underscore, as does a function named like the struct it attaches to
(cimgui's `ImDrawCmd_ImDrawCmd` is `ImDrawCmd.ImDrawCmd_`); a parameter gets `@`. Two C names that would become one C#
name are an error, never silently told apart.

## Running it by hand

The build writes each library's arguments to `<library>.bindings.rsp` in the project's intermediate folder (under
`Build/obj/<Configuration>/`, where its output is under `Build/bin/<Configuration>/`) and runs
`dotnet Build/bin/<Configuration>/Tools/NativeBindingsGenerator/NativeBindingsGenerator.dll @<that file>`; the same command reproduces a run. Errors come out in
MSBuild's form, naming the header line, so the build that ran it fails on them; the summary line counts what was made,
names the functions skipped, and points at the `.skipped.txt` beside the generated file that lists every skip. A
`.names.txt` beside it lists every C name and the C# name it became, struct fields (`SDL_Event.type`) and typedefs
to primitives (`SDL_WindowFlags` is `ulong`) included, for moving code from one to the other. The tool references nothing of Magic and is exempt from the engine's banned-symbol list,
as it reads headers and writes files.

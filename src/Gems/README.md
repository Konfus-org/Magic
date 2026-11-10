# Gems

The plugins that come with Magic. A project runs one by listing its name in its `.magic` file. To add one, see
[Templates](../../tools/README.md#templates).

## Gems that use other gems

A gem can use another gem's types. Reference its project with `Private="false"`, as with Core, so its assembly isn't
copied beside yours, and list both gems in the project. Magic loads the referenced gem first and gives your gem its one
copy, and refuses your gem, naming the other, when the project doesn't list it.

## Native libraries

A gem can call a C library. Declare it as a `NativeLibrary` item in the gem's project. The build fetches it, builds
it with CMake and generates its C# API with [ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp), which
the restore downloads. The item's metadata are documented in `Directory.Build.targets`.

- Built libraries are shared by every checkout on the machine, under `Magic/NativeLibs/` in local app data. Set
  `MagicNativeLibs` to move them and `MagicNativeCores` to limit the cores their build uses.
- The headers are read with the machine's own C headers. Call only what every platform declares.
- The API uses C's own names. Add `using static` for the library's class and the enums you use. Nothing is wrapped.
- A library several gems call goes in a project of its own, which they reference and no `.magic` file lists.
- A callback is an `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` method.
- List a declaration to leave out in the item's `Unbound`, with a comment saying why.
- To run the generator by hand: `ClangSharpPInvokeGenerator @<library>.bindings.rsp`, with the file the build writes
  in the project's intermediate folder.

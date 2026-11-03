# Gems

The plugins that come with Magic. A project runs one by listing its name in its `.magic` file. To add one, see
[Templates](../../tools/README.md#templates).

## Native libraries

A gem can call a C library. Declare it as a `NativeLibrary` item in the gem's project. The build fetches it, builds
it with CMake and generates its C# API with [ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp), which
the restore downloads. The item's metadata are documented in `Directory.Build.targets`.

- Built libraries are shared by every checkout on the machine, under `Magic/NativeLibs/` in local app data. Set
  `MagicNativeLibs` to move them and `MagicNativeCores` to limit the cores their build uses.
- The headers are read with the machine's own C headers. Call only what every platform declares.
- The API uses C's own names. Add `using static` for the library's class and the enums you use. Nothing is wrapped.
- A callback is an `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` method.
- List a declaration to leave out in the item's `Unbound`, with a comment saying why.
- To run the generator by hand: `ClangSharpPInvokeGenerator @<library>.bindings.rsp`, with the file the build writes
  in the project's intermediate folder.

# SlangShaderCompiler

Compiles shaders to SPIR-V and reads what each one takes. Provides `IShaderCompiler`.

It uses Khronos's [Slang](https://github.com/shader-slang/slang), its authors' release as a native library
([Native libraries](../README.md#native-libraries)). Slang's API is C++ COM interfaces, which the bindings generator
can't read, so the gem binds what it calls by hand.

- A shader is a Slang 2026 module. `import A.B` reads the shader asset at `Shaders/A/B.slang`, from any root
  ([Shaders](../../Resources/Shaders/README.md)). Warnings are errors, and errors name the file and line.
- A compile is one entry point, by name, or the module's only one. A generic entry point is specialized by the types
  the caller names, such as a surface's struct, from the modules it names.
- A shader's stage, the size of its constants and its group size come from Slang's reflection. A shader that reads a
  resource other than through the heap doesn't compile.
- Buffers and constants are laid out scalar, matrices row-major: each field right after the one before, as a
  sequential C# struct packs them.
- `ReflectStructAsync` reads a struct: each field's offset and size, and for a surface's or a sky's, the contract it
  implements and each param's type and initializer.

Compiled shaders are kept in the cache. Editing a shader, or anything it imports, compiles it again. Its one setting,
on `SlangShaderCompilerSettings` and shown in the settings window (F4), embeds debug information that lets a GPU capture
tool step through shaders.

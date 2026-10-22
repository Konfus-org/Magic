# DxcShaderCompiler

Compiles HLSL shaders to SPIR-V and reads what each one binds. Provides `IShaderCompiler`.

It uses Microsoft's [DirectXShaderCompiler](https://github.com/microsoft/DirectXShaderCompiler) (DXC) and Khronos's
[SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross), both native libraries
([Native libraries](../README.md#native-libraries)). SPIRV-Cross's C# API is generated. DXC is a COM API, bound by
hand in the gem.

Shaders are HLSL 2021 at Shader Model 6.0, with `main` as the entry point. Warnings are errors. `#include "path"`
reads the shader asset at `Shaders/path` ([Shaders](../../Resources/Shaders/README.md)). Errors name the file and line.

Compiled shaders are kept in the cache. Editing a shader, or anything it includes, compiles it again. Its one setting,
on `DxcShaderCompilerSettings` and shown in the settings window (F4), embeds debug information that lets a GPU capture
tool step through shaders.

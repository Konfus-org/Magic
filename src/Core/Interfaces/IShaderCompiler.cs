using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// Shaders to the bytecode a GPU takes, provided by a compiler gem. A shader is a Slang module, a <see cref="Shader"/>
/// asset, which imports others by their paths under <c>Shaders/</c> (<c>import Core.Heap;</c> is
/// <c>Shaders/Core/Heap.slang</c>), from any root; the compiler loads them (<see cref="Assets"/>). Safe from any
/// thread. It keeps what it compiled in the cache (<see cref="Caches"/>), so a shader compiles once rather than on
/// every run; the caller keeps the shaders it uses.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>
    /// <paramref name="entry"/> compiled into <paramref name="format"/> on a worker, with its stage, its constant
    /// block's size and a compute stage's group size; failures carry the compiler's text. Every resource it reads is
    /// the heap's (Shaders/Core/Heap.slang): one declared anywhere else fails. Warnings are errors.
    /// </summary>
    Task<Result<GpuShader>> CompileAsync(ShaderEntry entry, GpuShaderFormat format, CancellationToken cancel);

    /// <summary>
    /// What the struct <paramref name="type"/> is, such as a surface's or a sky's: the contract it implements and its
    /// fields, laid out as a buffer holds it; why not when its module does not compile or declares no such struct.
    /// </summary>
    Task<Result<ShaderStruct>> ReflectStructAsync(ShaderType type, CancellationToken cancel);
}

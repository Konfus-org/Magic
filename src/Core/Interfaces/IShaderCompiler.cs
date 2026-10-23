using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// HLSL to the bytecode a GPU takes, provided by a compiler gem. Safe from any thread. It keeps what it compiled in the
/// cache (<see cref="Caches"/>), so a shader compiles once rather than on every run; the caller keeps the shaders it
/// uses.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>
    /// <paramref name="hlsl"/>, entry point <c>main</c>, compiled for <paramref name="stage"/> into
    /// <paramref name="format"/> on a worker, with its constant block's size; failures carry the compiler's text,
    /// naming <paramref name="name"/>. Each <c>#include "path"</c> is the <see cref="Shader"/> asset at
    /// <c>Shaders/path</c>, which the compiler loads (<see cref="Assets"/>). <c>STAGE_VERTEX</c>,
    /// <c>STAGE_FRAGMENT</c> or <c>STAGE_COMPUTE</c> is defined in front of it. Every resource it reads is the heap's
    /// (Shaders/Include/Heap.hlsli): one declared anywhere else fails. HLSL 2021, Shader Model 6.0 with wave
    /// intrinsics, warnings as errors.
    /// </summary>
    Task<Result<GpuShader>> CompileAsync(
        string hlsl,
        string name,
        GpuStage stage,
        GpuShaderFormat format,
        CancellationToken cancel);

    /// <summary>
    /// The <see cref="Shader"/> asset at <paramref name="path"/> compiled as
    /// <see cref="CompileAsync(string, string, GpuStage, GpuShaderFormat, CancellationToken)"/> compiles its text,
    /// named by its path; why not, when no asset is there or it does not load.
    /// </summary>
    Task<Result<GpuShader>> CompileAsync(string path, GpuStage stage, GpuShaderFormat format, CancellationToken cancel);
}

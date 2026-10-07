using Magic.Contexts.Rendering;
using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// HLSL to the bytecode a device takes, exported by a compiler gem (SDL_shadercross today). Pure and thread-safe: it
/// keeps nothing, and the caller caches.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>
    /// <paramref name="hlsl"/> (entry point <c>main</c>) compiled for <paramref name="stage"/> into
    /// <paramref name="format"/> (<see cref="IRendering.ShaderFormat"/>), with its binding counts. The stage is defined
    /// in front of the source (<c>STAGE_VERTEX</c>, <c>STAGE_FRAGMENT</c> or <c>STAGE_COMPUTE</c>), which
    /// <c>Include/Bindings.hlsli</c> picks its registers by. The source is whole: its <c>#include</c>s are spliced in
    /// by the caller (<c>Hlsl.Splice</c>), nothing is read from disk. Failures carry the compiler's text.
    /// </summary>
    Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string format);
}

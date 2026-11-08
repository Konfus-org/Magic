using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// Compiles Slang entries to SPIR-V, as a gem provides it: the entry's module and the modules it imports come from the
/// assets, so a change to any of them is a new version and a new compile, and the result is cached under what it was
/// made from. Any thread; the work runs on a worker.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>
    /// <paramref name="entry"/> compiled, or why not: a message with the file and line of each error, logged once by
    /// the caller as it decides what to draw instead.
    /// </summary>
    Task<Result<CompiledShader>> CompileAsync(ShaderEntry entry, CancellationToken cancel);
}

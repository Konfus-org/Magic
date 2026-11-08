using System.Collections.Immutable;
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

    /// <summary>
    /// The fields of the struct <paramref name="type"/> names, as a buffer lays them out, the heap reading it so: how a
    /// renderer packs a material's params into the struct its shader reads them as. Or why not.
    /// </summary>
    Task<Result<ImmutableArray<ShaderField>>> ReflectAsync(ShaderTypeArgument type, CancellationToken cancel);

    /// <summary>
    /// The integer constant <paramref name="name"/> that the struct <paramref name="type"/> names declares, as
    /// <c>static const uint Levels = 2;</c> does: how a shader tells a renderer what it needs beyond its params. Or why
    /// not, as when the struct declares none.
    /// </summary>
    Task<Result<long>> ReflectConstantAsync(ShaderTypeArgument type, string name, CancellationToken cancel);
}

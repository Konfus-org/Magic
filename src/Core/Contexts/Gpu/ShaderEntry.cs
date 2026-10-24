using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// What <see cref="IShaderCompiler.CompileAsync"/> compiles: an entry point of a shader module, the function marked
/// <c>[shader("stage")]</c> named <paramref name="EntryPoint"/>, or the module's one entry point when none is named.
/// </summary>
public readonly record struct ShaderEntry(Handle<Shader> Module, string? EntryPoint = null)
{
    /// <summary>
    /// The types a generic entry point is specialized by, in its type parameters' order; none for one that is not.
    /// </summary>
    public IReadOnlyList<ShaderType> Types { get; init; } = [];

    /// <summary>
    /// Modules loaded beside the entry point's, whose types a type of <see cref="Types"/> names, as
    /// <c>Then&lt;Tonemap, Grade&gt;</c> names two; none when each type's own module is enough.
    /// </summary>
    public IReadOnlyList<Handle<Shader>> Modules { get; init; } = [];
}

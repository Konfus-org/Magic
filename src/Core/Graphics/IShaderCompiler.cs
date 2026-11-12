using System.Collections.Immutable;
using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// Compiles Slang entries to SPIR-V.
/// </summary>
/// <remarks>
/// The modules come from the assets, so a change to the entry's module or to a module it imports makes a new compile.
/// Results are cached. Any thread.
/// </remarks>
public interface IShaderCompiler
{
    /// <summary>
    /// Compiles <paramref name="entry"/>, or gives a message with the file and line of each error.
    /// </summary>
    /// <remarks>
    /// The caller logs the message one time, as it decides what to draw instead.
    /// </remarks>
    Task<Result<CompiledShader>> CompileAsync(ShaderEntry entry, CancellationToken cancel);

    /// <summary>
    /// Gets the fields of the struct that <paramref name="type"/> names, as a heap buffer lays them out, or why not.
    /// </summary>
    /// <remarks>
    /// A renderer uses them to pack a material's params into the struct that its shader reads.
    /// </remarks>
    Task<Result<ImmutableArray<ShaderField>>> ReflectAsync(ShaderTypeArgument type, CancellationToken cancel);

    /// <summary>
    /// Gets the integer constant <paramref name="name"/> of the struct that <paramref name="type"/> names, or why not.
    /// </summary>
    /// <remarks>
    /// A constant such as <c>static const uint Levels = 2;</c> tells a renderer what a shader needs beyond its params.
    /// </remarks>
    Task<Result<long>> ReflectConstantAsync(ShaderTypeArgument type, string name, CancellationToken cancel);
}

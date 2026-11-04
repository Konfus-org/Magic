using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Types;

namespace Magic.Shaders;

/// <summary>
/// The stage an entry point runs in.
/// </summary>
public enum ShaderStage : byte
{
    Compute,
    Vertex,
    Pixel,
}

/// <summary>
/// A type an entry is specialised with: the struct <paramref name="Type"/> in <paramref name="Module"/>, which
/// implements the interface the entry's generic parameter asks for.
/// </summary>
public readonly record struct ShaderTypeArgument(Handle<Shader> Module, string Type);

/// <summary>
/// One entry point to compile: a function of a <see cref="Shader"/> module, its stage, and the types it is specialised
/// with, in the order of its generic parameters. Equal entries compile to the same code, which is how the compiled
/// shader is cached.
/// </summary>
public readonly record struct ShaderEntry
{
    public ShaderEntry() { }

    public required Handle<Shader> Module { get; init; }

    public required string Name { get; init; }

    public required ShaderStage Stage { get; init; }

    public ImmutableArray<ShaderTypeArgument> TypeArguments { get; init; } = [];
}

/// <summary>
/// An entry compiled for the GPU: its SPIR-V, and what the renderer reads of its reflection.
/// </summary>
public sealed class CompiledShader
{
    public required ReadOnlyMemory<byte> Spirv { get; init; }

    public required string EntryPoint { get; init; }

    public required ShaderStage Stage { get; init; }

    /// <summary>
    /// A compute entry's threads per group along each axis; 0 for another stage.
    /// </summary>
    public uint GroupSizeX { get; init; }

    public uint GroupSizeY { get; init; }

    public uint GroupSizeZ { get; init; }
}

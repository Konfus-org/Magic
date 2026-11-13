using System.Collections.Immutable;
using Magic.Assets;

namespace Magic.Graphics;

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
/// One field of a shader's struct as a buffer lays it out: name, byte offset, size and struct name.
/// </summary>
/// <remarks>
/// <paramref name="Struct"/> names the field's struct, such as <c>TextureRef</c>. It is empty for a number or a
/// vector.
/// </remarks>
public readonly record struct ShaderField(string Name, uint Offset, uint Size, string Struct);

/// <summary>
/// One entry point to compile: a function of a <see cref="Shader"/> module, its stage and its type arguments.
/// </summary>
/// <remarks>
/// The type arguments are in the order of the generic parameters. Equal entries compile to the same code, so the
/// compiled shader is cached by entry.
/// </remarks>
public readonly record struct ShaderEntry
{
    public ShaderEntry() { }

    public required Handle<Shader> Module { get; init; }

    public required string Name { get; init; }

    public required ShaderStage Stage { get; init; }

    public ImmutableArray<ShaderTypeArgument> TypeArguments { get; init; } = [];

    /// <summary>
    /// The same entry of the same module in the same stage, with the same type arguments in the same order.
    /// </summary>
    public bool Equals(ShaderEntry other) =>
        Module == other.Module
        && Name == other.Name
        && Stage == other.Stage
        && TypeArguments.AsSpan().SequenceEqual(other.TypeArguments.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Module, Name, Stage);
}

/// <summary>
/// An entry compiled for the GPU: its SPIR-V and the reflection data a renderer needs.
/// </summary>
public sealed class CompiledShader
{
    public required ReadOnlyMemory<byte> Spirv { get; init; }

    public required string EntryPoint { get; init; }

    /// <summary>
    /// A compute entry's threads per group along each axis; 0 for another stage.
    /// </summary>
    public uint GroupSizeX { get; init; }

    public uint GroupSizeY { get; init; }

    public uint GroupSizeZ { get; init; }
}

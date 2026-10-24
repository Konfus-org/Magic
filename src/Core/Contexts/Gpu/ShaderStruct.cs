using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// The shader contract a struct implements: a surface's (<c>ISurface</c>) or a sky's (<c>ISky</c>), or neither.
/// </summary>
public enum ShaderContract : byte
{
    None,
    Surface,
    Sky,
}

/// <summary>
/// What a field is as a material's parameter: a scalar, or a vector of 2 to 4 of one, a colour (r, g, b, a in a .mat
/// file), or a texture, which a material names.
/// </summary>
public enum ShaderParamType : byte
{
    Float1,
    Float2,
    Float3,
    Float4,
    Int1,
    Int2,
    Int3,
    Int4,
    Uint1,
    Uint2,
    Uint3,
    Uint4,
    Bool,
    Color,
    Texture,
}

/// <summary>
/// A struct a shader declares, as <see cref="IShaderCompiler.ReflectStructAsync"/> reads it: the contract it
/// implements, its size and its fields in order, laid out as a buffer and a pass's constants hold it. A surface's or a
/// sky's fields are the parameters a material gives values for by name (Shaders/Core/Surface.slang, Sky.slang).
/// </summary>
public sealed record ShaderStruct(ShaderContract Contract, uint Size, IReadOnlyList<ShaderField> Fields);

/// <summary>
/// One field of a <see cref="ShaderStruct"/>: its name, where it sits and its size, in bytes; what it is as a
/// material's parameter, null for what a material cannot give a value for; and its initializer as the struct packs it,
/// empty for none, which is zero.
/// </summary>
public sealed record ShaderField(
    string Name,
    uint Offset,
    uint Size,
    ShaderParamType? Type,
    ReadOnlyMemory<byte> Default);

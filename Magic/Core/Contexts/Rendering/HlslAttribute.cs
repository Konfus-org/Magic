namespace Magic.Contexts.Rendering;

/// <summary>
/// Marks C# whose HLSL twin is written from it (<see cref="Services.Hlsl.Write"/>), so
/// the two cannot drift: shaders see it as it is declared here. A struct becomes an HLSL struct, its fields in order
/// with camelCase names (<see cref="Name"/> renames the struct); an enum becomes a <c>static const</c> per member,
/// named <see cref="Name"/> (else the enum's name) followed by the member's; a class has every <c>const</c> of it
/// written under its own name; a <c>const</c> field marked on its own is written as <see cref="Name"/>, as a
/// <c>#define</c> when <see cref="Define"/> (for what HLSL needs at compile time: <c>numthreads</c>, array sizes).
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Class | AttributeTargets.Field)]
public sealed class HlslAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;

    public bool Define { get; init; }
}

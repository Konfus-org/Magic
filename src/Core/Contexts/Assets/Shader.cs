using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// HLSL as written, its <c>#include</c>s still in it: each <c>#include "path"</c> is the shader at
/// <c>Shaders/path</c>, which <see cref="IShaderCompiler"/> loads as it compiles for the GPU it has. A surface
/// (<c>.surf.hlsl</c>) is what a <see cref="Material"/> names; the rest are a renderer's stages and the headers they
/// share.
/// </summary>
public sealed class Shader : ITextAsset<Shader>
{
    public static IReadOnlyList<string> Extensions { get; } = [".surf.hlsl", ".hlsl", ".hlsli"];

    public Handle<Shader> Handle { get; init; }

    public required string Text { get; init; }
}

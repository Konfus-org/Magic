namespace Magic.Assets;

/// <summary>
/// The source text of a Slang module, which a renderer compiles for its GPU.
/// </summary>
/// <remarks>
/// <c>import A.B;</c> loads the shader at <c>Shaders/A/B.slang</c>. An asset names a shader that implements a contract
/// of the shader library (<c>docs/SHADERS.md</c>), such as a material. The other shaders are the passes of a renderer
/// and the modules that they share.
/// </remarks>
[AssetFormat(AssetFileFormat.Text)]
public sealed class Shader : IAsset<Shader>
{
    public required string Text { get; init; }
}

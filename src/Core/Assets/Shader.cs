namespace Magic.Assets;

/// <summary>
/// A Slang module as written, which a renderer compiles for the GPU it has, loading the modules it imports:
/// <c>import A.B;</c> is the shader at <c>Shaders/A/B.slang</c>. One that implements a contract of the shader library
/// (<c>Shaders/README.md</c>), such as a material, is what an asset names; the rest are a renderer's passes and the
/// modules they share.
/// </summary>
[AssetFormat(AssetFileFormat.Text)]
public sealed class Shader : IAsset<Shader>
{
    public required string Text { get; init; }
}

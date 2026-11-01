namespace Magic.Assets.Types;

/// <summary>
/// A Slang module as written, which a renderer compiles for the GPU it has, loading the modules it imports:
/// <c>import A.B;</c> is the shader at <c>Shaders/A/B.slang</c>. A surface or a sky, a module whose struct named like
/// its file implements Shaders/Core/Surface.slang's <c>ISurface</c> or Sky.slang's <c>ISky</c>, is what a
/// <see cref="Material"/> names; the rest are a renderer's passes and the modules they share.
/// </summary>
[AssetFormat(AssetFileFormat.Text)]
public sealed class Shader : IAsset<Shader>
{
    public required string Text { get; init; }
}

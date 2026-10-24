using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// A Slang module as written, which <see cref="IShaderCompiler"/> compiles for the GPU it has, loading the modules it
/// imports: <c>import A.B;</c> is the shader at <c>Shaders/A/B.slang</c>. A surface or a sky, a module whose struct
/// named like its file implements Shaders/Core/Surface.slang's <c>ISurface</c> or Sky.slang's <c>ISky</c>, is what a
/// <see cref="Material"/> names, a mesh's or the world's <see cref="Components.Sky"/>; the rest are a renderer's passes
/// and the modules they share.
/// </summary>
public sealed class Shader : ITextAsset<Shader>
{
    public static IReadOnlyList<string> Extensions { get; } = [".slang"];

    public Handle<Shader> Handle { get; init; }

    public required string Text { get; init; }
}

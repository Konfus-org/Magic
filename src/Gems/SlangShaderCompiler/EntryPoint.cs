using Magic.Assets;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// The gem: it makes the <see cref="ShaderCompiler"/> over its <see cref="Settings"/>.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(App app, AssetManager assets, Caches caches, SlangShaderCompilerSettings settings)
    {
        Settings = settings;
        ShaderCompiler = new ShaderCompiler(app, assets, caches, settings);
        Track(ShaderCompiler);
    }

    public SlangShaderCompilerSettings Settings { get; }

    public ShaderCompiler ShaderCompiler { get; }
}

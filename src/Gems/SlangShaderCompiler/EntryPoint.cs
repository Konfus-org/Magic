using Magic.Assets;
using Magic.Files;
using Magic.Projects;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// The gem: it makes the <see cref="ShaderCompiler"/> over its <see cref="Settings"/>.
/// </summary>
public sealed class EntryPoint(App app, AssetManager assets, Caches caches, SlangShaderCompilerSettings settings)
    : IGem,
        IDisposable
{
    public SlangShaderCompilerSettings Settings { get; } = settings;

    public ShaderCompiler ShaderCompiler { get; } = new(app, assets, caches, settings);

    public void Dispose() => ShaderCompiler.Dispose();
}

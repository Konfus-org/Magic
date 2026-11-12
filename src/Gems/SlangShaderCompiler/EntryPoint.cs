using Magic.Assets;
using Magic.Files;
using Magic.Projects;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// The gem: it makes the <see cref="ShaderCompiler"/> over its <see cref="Settings"/>. It is public because nothing in
/// its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(App app, AssetManager assets, Caches caches, SlangShaderCompilerSettings settings)
    : IGem,
        IDisposable
{
    /// <summary>
    /// How shaders compile.
    /// </summary>
    public SlangShaderCompilerSettings Settings { get; } = settings;

    /// <summary>
    /// Compiles Slang entries to SPIR-V and caches them.
    /// </summary>
    public ShaderCompiler ShaderCompiler { get; } = new(app, assets, caches, settings);

    public void Dispose() => ShaderCompiler.Dispose();
}

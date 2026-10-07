using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;

namespace DeferredRendererGem;

/// <summary>
/// A shader as the render system uses it: its text, its include closure and the hash of that, and its surface when it is one. Rebuilt whole when it or an include changes.
/// </summary>
internal sealed record CachedShader(Shader Shader, string ClosureHash, ulong[] Closure, SurfaceSource? Surface);

/// <summary>
/// The shader text in use, by asset id (main thread), what compiles it, and where compiled bytecode is kept: a folder
/// of the project's cache per bytecode format, one file per entry, so a second start never runs the compiler.
/// Includes are spliced in from the assets (<see cref="Hlsl.Splice"/>). <see cref="DeferredSettings.ShaderCache"/>
/// turns the disk cache on and off live: it is asked at every compile.
/// </summary>
internal sealed class ShaderCache
{
    private readonly string _folder;
    private readonly DeferredSettings _settings;

    public ShaderCache(IShaderCompiler compiler, string folder, string format, DeferredSettings settings)
    {
        Compiler = compiler;
        _folder = folder;
        Format = format;
        _settings = settings;
    }

    /// <summary>
    /// The HLSL twins of the renderer's C# marked [Hlsl], which every source compiled includes: part of every cache key.
    /// The renderer never reloads, so they are written once.
    /// </summary>
    public static string Header { get; } = Hlsl.Write(typeof(ShaderCache).Assembly.GetTypes());

    public IShaderCompiler Compiler { get; }

    /// <summary>
    /// The folder this format's bytecode is kept in; null while the cache is off.
    /// </summary>
    public string? Folder => _settings.ShaderCache ? _folder : null;

    /// <summary>
    /// The device's bytecode format, part of every cache key.
    /// </summary>
    public string Format { get; }

    public Dictionary<ulong, CachedShader> Entries { get; } = [];

    /// <summary>
    /// What goes in front of a source: the generated header, and the line count started again so the compiler's
    /// lines are the source's own.
    /// </summary>
    public static string Preamble(string name)
    {
        return $"{Header}#line 1 \"{name}\"\n";
    }
}

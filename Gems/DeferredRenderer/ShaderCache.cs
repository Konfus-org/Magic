using Magic.Contexts.Assets;

namespace DeferredRendererGem;

/// <summary>
/// A shader as the render system uses it: its text, its include closure and the hash of that, and its surface when it is one. Rebuilt whole when it or an include changes.
/// </summary>
internal sealed record CachedShader(Shader Shader, string ClosureHash, ulong[] Closure, SurfaceSource? Surface);

/// <summary>
/// The shader text in use, by asset id (main thread), and where compiled bytecode is kept on disk: under the project's
/// cache, one folder per bytecode format, so a second start never runs the compiler. <see cref="IncludeDirectory"/> is
/// the shader root <c>#include</c>s resolve against. <see cref="DeferredSettings.ShaderCache"/> turns the disk cache on
/// and off live: it is asked at every compile.
/// </summary>
internal sealed class ShaderCache
{
    private readonly string _cacheDirectory;
    private readonly DeferredSettings _settings;

    public ShaderCache(string includeDirectory, string cacheDirectory, string format, DeferredSettings settings)
    {
        IncludeDirectory = includeDirectory;
        _cacheDirectory = cacheDirectory;
        Format = format;
        _settings = settings;
    }

    public string IncludeDirectory { get; }

    /// <summary>
    /// Null when the cache is off.
    /// </summary>
    public string? CacheDirectory => _settings.ShaderCache ? _cacheDirectory : null;

    /// <summary>
    /// The device's bytecode format, part of every cache key.
    /// </summary>
    public string Format { get; }

    public Dictionary<ulong, CachedShader> Entries { get; } = [];
}

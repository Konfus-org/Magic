using Magic.Contexts.Assets;

namespace Magic.Contexts.Rendering;

/// <summary>
/// A shader as the render system uses it: its text, its include closure and the hash of that, and its surface when it is one. Rebuilt whole when it or an include changes.
/// </summary>
internal sealed record CachedShader(Shader Shader, string ClosureHash, ulong[] Closure, SurfaceSource? Surface);

/// <summary>
/// The shader text in use, by asset id (main thread), and where compiled bytecode is kept on disk: under the project's
/// cache, one folder per bytecode format, so a second start never runs the compiler. <see cref="IncludeDirectory"/> is
/// the shader root <c>#include</c>s resolve against.
/// </summary>
internal sealed class ShaderCache
{
    public ShaderCache(string includeDirectory, string? cacheDirectory, string format)
    {
        IncludeDirectory = includeDirectory;
        CacheDirectory = cacheDirectory;
        Format = format;
    }

    public string IncludeDirectory { get; }

    /// <summary>
    /// Null when the cache is off.
    /// </summary>
    public string? CacheDirectory { get; }

    /// <summary>
    /// The device's bytecode format, part of every cache key.
    /// </summary>
    public string Format { get; }

    public Dictionary<ulong, CachedShader> Entries { get; } = [];
}

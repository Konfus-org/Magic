using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// A C# script: a <c>.cs</c> file whose class, named like the file, is an <see cref="IScript"/>. The project's code, or
/// the gem it belongs to, compiles it; the asset is that class, found among the code loaded, which
/// <see cref="IScripts"/> makes scripts of.
/// </summary>
public sealed class Script : IAsset<Script>
{
    public static IReadOnlyList<string> Extensions { get; } = [".cs"];

    public Handle<Script> Handle { get; init; }

    /// <summary>
    /// The class the file declares.
    /// </summary>
    public required Type Class { get; init; }
}

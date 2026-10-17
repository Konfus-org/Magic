using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// A C# script: a <c>.cs</c> file whose class, named like the file, is an <see cref="IScript"/>. The project's code, or
/// the gem it belongs to, compiles it; the gem that runs scripts reads the asset as that class, found by its name in
/// the code loaded (<see cref="Services.Assemblies"/>). A prefab names a script by this asset's id, as it names any
/// asset, so the file can be renamed with its class and nothing that names it notices; the project's are read again
/// when its code is built.
/// </summary>
public sealed class Script : IAsset<Script>
{
    public static IReadOnlyList<string> Extensions { get; } = [".cs"];

    public Handle<Script> Handle { get; init; }

    /// <summary>
    /// The class the file declares.
    /// </summary>
    public required Type Type { get; init; }
}

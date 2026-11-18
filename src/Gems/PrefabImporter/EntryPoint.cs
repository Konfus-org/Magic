using Magic.Assets;
using Magic.Projects;

namespace Magic.PrefabImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads <c>.prefab</c> files and the <see cref="Exporter"/> that
/// writes them.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    public Importer Importer { get; } = new(assets);

    public Exporter Exporter { get; } = new(assets);
}

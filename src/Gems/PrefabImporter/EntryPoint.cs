using Magic.Assets;
using Magic.Projects;

namespace Magic.PrefabImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads <c>.prefab</c> files. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    /// <summary>
    /// Reads <c>.prefab</c> files into prefabs.
    /// </summary>
    public Importer Importer { get; } = new(assets);
}

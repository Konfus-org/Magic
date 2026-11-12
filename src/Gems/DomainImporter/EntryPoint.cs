using Magic.Assets;
using Magic.Projects;

namespace Magic.DomainImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads <c>.domain</c> files.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    public Importer Importer { get; } = new(assets);
}

using Magic.Projects;

namespace Magic.SdlTextureImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads image files into textures and the <see cref="Exporter"/> that
/// writes them as PNGs. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project
/// lists the gem.
/// </summary>
public sealed class EntryPoint : IGem
{
    /// <summary>
    /// Reads image files into textures, and keeps what it made in the cache.
    /// </summary>
    public Importer Importer { get; } = new();

    /// <summary>
    /// Writes a plain texture as a PNG.
    /// </summary>
    public Exporter Exporter { get; } = new();
}

using Magic.Projects;

namespace Magic.SdlTextureImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads image files into textures and the <see cref="Exporter"/> that
/// writes them as PNGs.
/// </summary>
public sealed class EntryPoint : IGem
{
    public Importer Importer { get; } = new();

    public Exporter Exporter { get; } = new();
}

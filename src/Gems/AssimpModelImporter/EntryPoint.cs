using Magic.Projects;

namespace Magic.AssimpModelImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads model files.
/// </summary>
public sealed class EntryPoint : IGem
{
    public Importer Importer { get; } = new();
}

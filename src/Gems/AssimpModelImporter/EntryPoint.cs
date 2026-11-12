using Magic.Projects;

namespace Magic.AssimpModelImporter;

/// <summary>
/// The gem: it makes the <see cref="Importer"/> that reads model files. It is public because nothing in its assembly makes
/// it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint : IGem
{
    /// <summary>
    /// Reads FBX, glTF and OBJ files into models.
    /// </summary>
    public Importer Importer { get; } = new();
}

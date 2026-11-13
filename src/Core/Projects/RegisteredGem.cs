using System.Linq;
using System.Reflection;

namespace Magic.Projects;

/// <summary>
/// A gem found and registered: what it is and what it provides, before it is made.
/// </summary>
/// <param name="Name">The gem's name, as the project lists it.</param>
/// <param name="Author">Who made it; its name when its project says nothing.</param>
/// <param name="Description">What it is, in a sentence; empty when its project says nothing.</param>
/// <param name="Version">Its version; 1.0.0 when its project says nothing.</param>
/// <param name="Type">Its one <see cref="IGem"/> class.</param>
/// <param name="Parts">The public instance properties of its class, each a part the class makes.</param>
/// <param name="Provides">The interfaces it provides to other gems, by its parts.</param>
/// <param name="References">The listed gems its code references, which it loads after.</param>
/// <param name="Resources">The folder of its own assets; null when it has none.</param>
/// <param name="Folder">The folder its assemblies load from.</param>
internal sealed record RegisteredGem(
    string Name,
    string Author,
    string Description,
    Version Version,
    Type Type,
    IReadOnlyList<PropertyInfo> Parts,
    IReadOnlyList<Type> Provides,
    IReadOnlyList<string> References,
    string? Resources,
    string Folder
)
{
    /// <summary>
    /// Each part's class.
    /// </summary>
    public IEnumerable<Type> PartTypes => Parts.Select(part => part.PropertyType);
}

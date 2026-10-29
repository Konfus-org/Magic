using System.Reflection;

namespace Magic.Host;

/// <summary>
/// A gem's assembly as loaded, and what making the gem takes.
/// </summary>
/// <param name="Name">The gem's name, as the project lists it.</param>
/// <param name="Path">Where its assembly was loaded from.</param>
/// <param name="Author">Who made it; its name when its project says nothing.</param>
/// <param name="Description">What it is, in a sentence; empty when its project says nothing.</param>
/// <param name="Version">Its version; 1.0.0 when its project says nothing.</param>
/// <param name="Type">Its one <see cref="IGem"/> class.</param>
/// <param name="Provides">The interfaces it provides to other gems.</param>
/// <param name="Assembly">Its assembly.</param>
internal readonly record struct LoadedGem(
    string Name,
    string Path,
    string Author,
    string Description,
    Version Version,
    Type Type,
    List<Type> Provides,
    Assembly Assembly
);

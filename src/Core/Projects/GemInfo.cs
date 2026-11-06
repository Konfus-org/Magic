using System.Runtime.Loader;

namespace Magic.Projects;

/// <summary>
/// A gem found and registered: what it is and what it provides, before it is made.
/// </summary>
/// <param name="Name">The gem's name, as the project lists it.</param>
/// <param name="Author">Who made it; its name when its project says nothing.</param>
/// <param name="Description">What it is, in a sentence; empty when its project says nothing.</param>
/// <param name="Version">Its version; 1.0.0 when its project says nothing.</param>
/// <param name="Type">Its one <see cref="IGem"/> class.</param>
/// <param name="Provides">The interfaces it provides to other gems.</param>
/// <param name="Resources">The folder of its own assets; null when it has none.</param>
/// <param name="Folder">The folder its assemblies load from.</param>
/// <param name="Context">
/// Where its assemblies load, held from the moment they do: nothing else keeps it alive, and once collected it would start
/// unloading the gem.
/// </param>
internal sealed record GemInfo(
    string Name,
    string Author,
    string Description,
    Version Version,
    Type Type,
    IReadOnlyList<Type> Provides,
    string? Resources,
    string Folder,
    AssemblyLoadContext Context
);

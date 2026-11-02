using Magic.Pipelines;

namespace Magic.GemAPI;

/// <summary>
/// A gem found and registered: what it is, what it provides and what it needs, before it is made.
/// </summary>
/// <param name="Name">The gem's name, as the project lists it.</param>
/// <param name="Author">Who made it; its name when its project says nothing.</param>
/// <param name="Description">What it is, in a sentence; empty when its project says nothing.</param>
/// <param name="Version">Its version; 1.0.0 when its project says nothing.</param>
/// <param name="Type">Its one <see cref="IGem"/> class.</param>
/// <param name="Provides">The interfaces it provides to other gems.</param>
/// <param name="Reads">The asset types it reads, one for each asset loader it implements.</param>
/// <param name="Joins">The states of the pipelines it joins, one for each <see cref="IPipeline{TState}"/> it implements.</param>
/// <param name="Needs">The names of the registered gems its constructor asks for.</param>
/// <param name="Resources">The folder of its own assets; null when it has none.</param>
internal sealed record GemInfo(
    string Name,
    string Author,
    string Description,
    Version Version,
    Type Type,
    IReadOnlyList<Type> Provides,
    IReadOnlyList<Type> Reads,
    IReadOnlyList<Type> Joins,
    IReadOnlyList<string> Needs,
    string? Resources
);

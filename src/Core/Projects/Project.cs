using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Projects;

/// <summary>
/// A project as its <c>.magic</c> file describes it.
/// </summary>
public readonly record struct Project
{
    public required string Name { get; init; }

    public required ImmutableArray<string> Gems { get; init; }

    /// <summary>
    /// The domain the run opens first; none opens the default one.
    /// </summary>
    public Handle<Domain> EntryPoint { get; init; }

    /// <summary>
    /// What shows while a domain replaces the world; none shows the engine's.
    /// </summary>
    public Handle<Domain> LoadingScreen { get; init; }

    /// <summary>
    /// What is put at a domain's spawn as it replaces the world; none puts nothing.
    /// </summary>
    public Handle<Prefab> Player { get; init; }

    /// <summary>
    /// The <c>.magic</c> file it was read from, set by the open command. It is not stored in the file.
    /// </summary>
    [JsonIgnore]
    public string Path { get; init; }
}

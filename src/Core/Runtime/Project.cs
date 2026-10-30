using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Magic.Runtime;

/// <summary>
/// A project as its <c>.magic</c> file describes it.
/// </summary>
public readonly record struct Project
{
    public required string Name { get; init; }

    public required ImmutableArray<string> Gems { get; init; }

    /// <summary>
    /// The <c>.magic</c> file it was read from, which <see cref="ProjectExtensions"/>' <c>Load</c> gives it; never
    /// read from the file.
    /// </summary>
    [JsonIgnore]
    public string Path { get; init; }
}

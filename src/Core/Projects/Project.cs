using System.Collections.Immutable;
namespace Magic.Projects;

public readonly record struct Project
{
    public required string Name { get; init; }
    public required ImmutableArray<string> Gems { get; init; }
}

using System.Collections.Immutable;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.Worlds;

/// <summary>
/// A game save as its file holds it, and the world a restart keeps: the domains open, what changed against their files,
/// and the pause and time scale.
/// </summary>
internal sealed record SaveFile(
    ImmutableArray<Handle<Domain>> Domains,
    WorldChanges Changes,
    bool IsPaused,
    float TimeScale
);

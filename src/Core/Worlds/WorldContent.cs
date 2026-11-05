using Magic.Files;
using Magic.Files.Types;

namespace Magic.Worlds;

/// <summary>
/// Something that enters or leaves the world as one: a domain, part of one, a spawned asset or a saved world. A gem keeps
/// its own part of the world under <paramref name="Id"/>.
/// </summary>
/// <param name="Id">What it is for the run: the same on what enters as on what later leaves.</param>
/// <param name="Domain">The domain it is or is part of; none for a spawned asset.</param>
public readonly record struct WorldContent(ulong Id, Handle<Domain> Domain);

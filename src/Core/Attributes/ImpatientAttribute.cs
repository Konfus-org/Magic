using Magic.Interfaces;
using Magic.Services;

namespace Magic.Attributes;

/// <summary>
/// Marks a script (<see cref="IScript"/>) that starts as soon as it is attached to its entity
/// (<see cref="World.Attach"/>), even while something spawns or a domain loads in place of the world
/// (<see cref="World.IsLoading"/>). Without it a script waits until what spawned with its entity has all spawned, and
/// the world is there, so it never sees a world that is half there: right for what plays, wrong for the loading
/// domain's own scripts, such as a progress bar, which never run otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ImpatientAttribute : Attribute;

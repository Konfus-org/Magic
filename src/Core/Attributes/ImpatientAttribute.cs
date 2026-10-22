using Magic.Interfaces;
using Magic.Services;

namespace Magic.Attributes;

/// <summary>
/// Marks a script (<see cref="IScript"/>) that starts and runs even while a domain loads in place of the world
/// (<see cref="World.IsLoading"/>) and the loading domain shows. Without it a script attached then
/// (<see cref="World.Attach"/>) starts only as the new domain is readied, all of it there, so it never sees a world that
/// is half there, and runs only from the frame after the world is first shown, as does one put back with a saved world
/// (<see cref="World.Restore"/>): right for what plays, wrong for the loading domain's own scripts, such as a progress
/// bar, which never run otherwise. One that starts as the new domain is readied, out of sight, waits all the same, with
/// or without it: it belongs to the world not shown yet, so nothing of it shows over the loading domain's last image.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ImpatientAttribute : Attribute;

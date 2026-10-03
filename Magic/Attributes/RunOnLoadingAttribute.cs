namespace Magic.Attributes;

/// <summary>
/// Marks a script that is made, and run, as soon as its entity spawns, even while the world is still loading behind
/// the loading domain. Without it a script waits until the loading domain has closed, so it never sees a world that
/// is half there: right for what plays, wrong for the loading screen's own scripts, for what prepares the world or
/// must exist from the start.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RunOnLoadingAttribute : Attribute
{
}

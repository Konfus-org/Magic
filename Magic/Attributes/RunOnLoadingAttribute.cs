namespace Magic.Attributes;

/// <summary>
/// Marks a script that is made, and run, as soon as its entity spawns, even while its domain is still loading behind
/// the loading domain. Without it a script of such a domain waits until the domain is loaded, so it never sees a
/// world that is half there: right for what plays, wrong for what prepares the world or must exist from the start.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RunOnLoadingAttribute : Attribute
{
}

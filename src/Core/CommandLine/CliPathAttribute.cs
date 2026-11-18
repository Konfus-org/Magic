namespace Magic.CommandLine;

/// <summary>
/// Gives the console the public properties of a part of a gem under <paramref name="path"/>: with
/// <c>[CliPath("Window")]</c>, <c>query Window.Title</c> reads a property, and <c>set Window.Title</c> writes one that
/// has a public setter.
/// </summary>
/// <remarks>
/// Put it on the class of the part, or on an interface that the part provides. The console shows the properties of
/// the type that has the attribute, and no others. Use it for a value that is state of the run. A value that a user
/// keeps is a setting. Give a path that no category of settings and no tag of an entity has, or the console finds
/// those first.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class CliPathAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

namespace Magic.Attributes;

/// <summary>
/// Marks a class as a gem and carries its metadata. The loader constructs exactly one instance per gem
/// assembly, resolving every constructor parameter from the host container (host services or services
/// other gems export). Implement <see cref="IDisposable"/> to run cleanup when the gem is unloaded.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GemAttribute(string name, string version, string description) : Attribute
{
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string Description { get; } = description;
    public string? Author { get; init; }
}

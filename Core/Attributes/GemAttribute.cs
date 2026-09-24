namespace Magic.Attributes;

/// <summary>
/// Marks the one class in a gem assembly that is the gem. The host constructs it once, resolving every
/// constructor parameter from the container (host services or services other gems export); those parameters
/// are the gem's dependencies, and it loads after them and unloads before them. Implement
/// <see cref="IDisposable"/> to clean up on unload, <see cref="Interfaces.IHotReloadable"/> to keep state across a hot reload.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GemAttribute(string name, string version, string description, string author, bool isStatic = false) : Attribute
{
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string Description { get; } = description;
    public string? Author { get; } = author;

    /// <summary>
    /// A static gem is loaded once and stays until shutdown: changes to its file on disk are ignored until
    /// the next start. Use it for the systems everything else is built on (windowing, ECS, logging), which
    /// the host itself holds on to. The gems it depends on cannot be reloaded under it either.
    /// </summary>
    public bool Static { get; } = isStatic;

    /// <summary>
    /// Names of gems this one needs loaded, on top of what its constructors need from the container: for a
    /// gem that owns process-wide state (a library's init and quit) that this one uses without any service
    /// between them. It loads after them and unloads before them.
    /// </summary>
    public string[] DependsOn { get; init; } = [];
}

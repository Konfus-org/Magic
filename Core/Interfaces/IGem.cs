using DryIoc;

namespace Core.Interfaces;

/// <summary>
/// Gems are the core components of the Magic framework.
/// They are modular units of functionality that can be loaded, unloaded, and reloaded at runtime.
/// </summary>
public interface IGem
{
    /// <summary>
    /// Resolve host services here, and register any services this gem provides to the host and to other gems.
    /// Everything registered is unregistered automatically when the gem is unloaded.
    /// </summary>
    void OnLoad(IContainer services);
    void OnUnload();
    void OnReloading(byte[] persist);
    void OnReloaded(byte[] restore);
}

internal readonly record struct GemMetadata(
    string Name = "",
    string Version = "",
    string Author = "",
    string Description = "",
    string[] Dependencies = default!);

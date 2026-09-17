using DryIoc;

namespace Core.GemAPI;

/// <summary>
/// Gems are the core components of the Magic framework.
/// They are modular units of functionality that can be loaded, unloaded, and reloaded at runtime.
/// </summary>
public interface IGem
{
    /// <summary>
    /// Called once, before <see cref="OnLoad"/>. Register any services this gem provides to the host and to other gems.
    /// Everything registered here is unregistered automatically when the gem is unloaded.
    /// </summary>
    void Register(IRegistrator services);
    void OnLoad(IResolver services);
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

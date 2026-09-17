namespace Core.GemAPI;

/// <summary>
/// Gems are the core components of the Magic framework.
/// They are modular units of functionality that can be loaded, unloaded, and reloaded at runtime.
/// </summary>
public interface IGem
{
    void OnLoad();
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

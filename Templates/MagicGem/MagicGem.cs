using Magic.Attributes;
using Magic.Services;

namespace MagicGem;

/// <summary>
/// Constructor parameters are this gem's dependencies: host services (SystemRegistry, Directories, ...) or
/// services other gems export. The host loads gems in dependency order, so they are always there.
/// To offer a service to the host and other gems, put [GemExport] on a class implementing a host interface.
/// </summary>
[Gem("MagicGem", "1.0.0", "GEM_DESCRIPTION", Author = "GEM_AUTHOR")]
internal sealed class MagicGem : IDisposable
{
    public MagicGem(SystemRegistry systems)
    {
        // Runs when the gem is loaded. To run something every frame:
        // _system = systems.Register(deltaTime => { ... }, UpdateType.Update); and dispose it in Dispose().
    }

    // Hot reload: return the state to keep before the gem is unloaded...
    //[OnGemReloading]
    //private byte[] Save() => [];

    // ...and get it back once the rebuilt gem has been constructed.
    //[OnGemReloaded]
    //private void Restore(byte[] state) { }

    public void Dispose()
    {
        // Runs when the gem is unloaded. Exports implementing IDisposable are disposed by the host.
    }
}

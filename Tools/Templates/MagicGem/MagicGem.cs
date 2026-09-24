using Magic.Attributes;
using Magic.Services;

namespace MagicGem;

/// <summary>
/// Constructor parameters are this gem's dependencies: host services (SystemScheduler, Project, ...) or
/// services other gems export. The host loads gems in dependency order, so they are always there.
/// To offer a service to the host and other gems, put [GemExport(typeof(IHostInterface))] on a class implementing it.
/// Static = true on [Gem] makes a gem that loads once and never hot reloads, for the systems the host itself
/// holds on to. Implement IHotReloadable to carry state across a hot reload.
/// DependsOn = ["Other Gem"] on [Gem] names gems that must be loaded first without a service between them.
/// </summary>
[Gem("MagicGem", "1.0.0", "GEM_DESCRIPTION", "GEM_AUTHOR")]
internal sealed class MagicGem : IDisposable
{
    public MagicGem(SystemScheduler systems)
    {
        // Runs when the gem is loaded. To run something every frame:
        //   _system = systems.Schedule(deltaTime => { ... }, UpdateType.Update);
        // Keep the handle and dispose it in Dispose: the host does not track what a gem registers, and a handle
        // left behind keeps the old assembly alive after a hot reload (the host logs a warning when that happens).

        // To work with entities, take IWorld (from the Flecs gem) as a constructor parameter and use IWorld.Schedule.
        // Scheduled systems go with the gem too.
    }

    // Hot reload: implement Magic.Interfaces.IHotReloadable to return the state to keep before the gem is
    // unloaded (byte[] Persist()) and get it back once the rebuilt gem has been constructed (void Restore(byte[] state)).

    public void Dispose()
    {
        // Runs when the gem is unloaded. Exports implementing IDisposable are disposed by the host.
    }
}

using Magic.Contexts;
using Magic.Interfaces;

namespace MyMagicGem;

/// <summary>
/// The gem: the one class in this dll that implements IGem. Constructor parameters are its dependencies: host
/// services (Project, Assets, Events, IFileSystem, Scheduler, World) or interfaces other gems provide, such as IEcs from the ECS gem.
/// The host loads gems in dependency order, so they are always there. To offer a service to the host and other gems,
/// implement its Core interface on this class (IAssetLoader&lt;T&gt;, IOverlay, ...). Name, static and dependencies
/// on other gems by name are set in the csproj (GemStatic, GemDependsOn).
/// </summary>
internal sealed class MyMagicGem : IGem
{
    public MyMagicGem()
    {
        // Runs when the gem is loaded. To work with entities, take IEcs as a constructor parameter, build a query
        // here and run it in Update. A component this gem declares is a plain struct implementing
        // Magic.Contexts.Components.IComponent; chunk files then name it by its type name, nothing registers it.
    }

    public void Dispose()
    {
        // Runs when the gem is unloaded. Dispose every handle taken from a host service here (an Events.Watch, an
        // ECS query): the host tracks none of them, and one left behind keeps the old assembly alive after a reload.
    }

    /// <summary>Once a frame, after the gems it depends on. frame.Delta is in seconds; frame.Events holds what happened since the last frame.</summary>
    public void Update(in Frame frame)
    {
    }

    // Also available: FixedUpdate, LateUpdate and Render (same shape), and Reloading/Reloaded to carry state across a
    // hot reload (byte[] Reloading() on the old instance, void Reloaded(byte[] state) on the rebuilt one).
}

using Magic.Utils;

namespace FlecsEcsGem;

/// <summary>
/// A scheduled system or observer. Ending its subscription destroys the flecs entity. Whoever scheduled it keeps the
/// subscription and disposes it when done (a gem in its own Dispose); otherwise the ECS would keep calling into an
/// unloaded assembly, and keep it alive.
/// </summary>
internal static class FlecsSystem
{
    public static Subscription Subscribe(FlecsEcs ecs, ulong id)
    {
        return new Subscription(() =>
        {
            // Flecs.NET.Debug aborts on a dead entity rather than throwing, so check first.
            if (!ecs.IsDisposed && ecs.Native.IsAlive(id))
                ecs.Native.Entity(id).Destruct();
        });
    }
}

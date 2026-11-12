using Magic.Projects;
using Magic.Threading;

namespace Magic.ArchEcs;

/// <summary>
/// The gem: it makes the entity world, <see cref="Ecs"/>. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(Threads threads) : IGem, IDisposable
{
    /// <summary>
    /// The entity world.
    /// </summary>
    public Ecs Ecs { get; } = new(threads);

    public void Dispose() => Ecs.Dispose();
}

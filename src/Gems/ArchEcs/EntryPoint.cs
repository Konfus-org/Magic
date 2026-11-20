using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ArchEcs;

/// <summary>
/// The gem: it makes the entity world, <see cref="Ecs"/>.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(Threads threads)
    {
        Ecs = new Ecs(threads);
        Track(Ecs);
    }

    public Ecs Ecs { get; }
}

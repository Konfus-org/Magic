using Magic.Projects;
using Magic.Threading;

namespace Magic.ArchEcs;

/// <summary>
/// The gem: it makes the entity world, <see cref="Ecs"/>.
/// </summary>
public sealed class EntryPoint(Threads threads) : IGem, IDisposable
{
    public Ecs Ecs { get; } = new(threads);

    public void Dispose() => Ecs.Dispose();
}

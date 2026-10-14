using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// A script: what an entity does, beside the data its components hold. A class implementing it, named like its
/// <c>.cs</c> file, is a <see cref="Script"/> asset. A prefab's part holds scripts as it holds components, with their
/// values set, and each entity spawned from the part is given its own copy of each (<see cref="World.Attach"/>),
/// started on it, until the entity is destroyed, when it is disposed if it is <see cref="IDisposable"/>.
/// <para>
/// Its constructor takes the services it needs, as a gem's does, and never its entity: a script in a prefab is made
/// before there is one. Its public <c>{ get; set; }</c> properties are its values, which a prefab sets, and their
/// initialisers its defaults. <see cref="Start"/> is where it begins on its entity. A script that is an
/// <see cref="IBehavior"/> has hooks each frame; one that is also a system, of any kind <see cref="IEcs"/> takes, is
/// added to the ECS while its entity lives.
/// </para>
/// </summary>
public interface IScript
{
    /// <summary>
    /// Begins on <paramref name="entity"/>, once this copy is attached to it with its values set, with the entity's
    /// components there to read. Main thread.
    /// </summary>
    void Start(Entity entity)
    {
    }
}

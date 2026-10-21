using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// A script: what an entity does, beside the data its components hold. A class implementing it, named like its
/// <c>.cs</c> file, is a <see cref="Script"/> asset. A prefab's part holds its scripts as it holds components, each
/// with the values the prefab gives it, and each entity spawned from the part is given its own copy of each
/// (<see cref="World.Attach"/>), made for it, started on it, and run until the entity is destroyed, when it is disposed
/// if it is <see cref="IDisposable"/>.
/// <para>
/// Its constructor takes the entity it is attached to first, when it uses it, then the services it needs, as a gem's
/// does, and keeps them, nothing more: <c>Hover(Entity owner, World world)</c>. One with services alone, or none, is
/// fine. Its public
/// <c>{ get; set; }</c> properties are its values, which a prefab sets after it is made, and their initialisers its
/// defaults.
/// <see cref="Start"/> is where it begins, with its values set and its entity's components there to read. A script that
/// is an <see cref="IBehavior"/> has hooks each frame; one that is also a system (<see cref="ISystemScript{T1}"/>,
/// <see cref="IParallelSystemScript{T1}"/> and the rest) is added to the ECS while its entity lives.
/// </para>
/// </summary>
public interface IScript
{
    /// <summary>
    /// Begins on its entity, once this copy is made for it with its values set, with the entity's components there to
    /// read. Main thread.
    /// </summary>
    void Start()
    {
    }
}

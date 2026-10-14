using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The scripts (<see cref="IScript"/>): makes them, as a prefab's are made with its values set, and runs a copy of one
/// for each entity it is attached to until that entity is destroyed. An <see cref="IBehavior"/> has its hooks called each
/// frame, less often far from the player; a script that is also a system, of any kind, is added to the
/// <see cref="IEcs"/>.
/// </summary>
public interface IScripts
{
    /// <summary>
    /// A new script of <paramref name="script"/>'s class, its constructor given the services it asks for, its properties
    /// at their defaults; why not, when it has other than one public constructor, asks for what nothing provides, or
    /// throws. Any thread: a constructor only keeps what it is given.
    /// </summary>
    Result<IScript> Make(Script script);

    /// <summary>
    /// Gives <paramref name="entity"/> its own copy of <paramref name="script"/>, as a part's component is copied to
    /// each entity spawned from it: made as <see cref="Make"/> makes one, given <paramref name="script"/>'s public
    /// property values, then started on the entity (<see cref="IScript.Start"/>) here and now, and run until the entity
    /// is destroyed. <see cref="World.Attach"/> calls it once whatever spawned with the entity is all there. One that
    /// cannot be made, or throws as it starts, is an error, and the entity goes without it. Main thread.
    /// </summary>
    void Attach(Entity entity, IScript script);
}

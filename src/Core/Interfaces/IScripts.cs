using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The scripts (<see cref="IScript"/>): makes them, and runs a copy of one for each entity it is attached to, made for
/// that entity with the values a prefab gives it, until the entity is destroyed. An <see cref="IBehavior"/> has its
/// hooks called each frame, less often far from the player; a script that is also a system, of any kind, is added to
/// the <see cref="IEcs"/>.
/// </summary>
public interface IScripts
{
    /// <summary>
    /// A new script of <paramref name="script"/>'s class that runs on no entity, as a pipeline script does: its
    /// constructor given the services it asks for, its properties at their defaults; why not, when it has other than one
    /// public constructor, asks for an entity or for what nothing provides, or throws. Any thread: a constructor only
    /// keeps what it is given.
    /// </summary>
    Result<IScript> Make(Script script);

    /// <summary>
    /// Gives <paramref name="entity"/> its own copy of <paramref name="script"/>, as a part's component is copied to
    /// each entity spawned from it: made for the entity, its constructor given it first and then the services it asks
    /// for, given the script's values, then started (<see cref="IScript.Start"/>) here and now, and run until the entity
    /// is destroyed. <see cref="World.Attach"/> calls it once whatever spawned with the entity is all there. One that
    /// cannot be made, or throws as it starts, is an error, and the entity goes without it. Main thread.
    /// </summary>
    void Attach(Entity entity, PrefabScript script);

    /// <summary>
    /// Adds the scripts on <paramref name="entity"/> to <paramref name="into"/>, each as it is now: its values, its
    /// public settable properties as a prefab names them, and what it kept in its own fields, by name. Not what its
    /// constructor was handed, a service, nor what is made rather than data, a delegate, a task or a disposable, nor
    /// what JSON cannot hold, which is warned about once per class: a copy put back starts over without those. Main
    /// thread.
    /// </summary>
    void Save(Entity entity, ICollection<ScriptState> into);

    /// <summary>
    /// Puts <paramref name="script"/> back on <paramref name="entity"/> as <paramref name="state"/> saved it
    /// (<see cref="Save"/>): made for the entity as <see cref="Attach"/> makes one, its values and fields set back, and
    /// run until the entity is destroyed, but not started, as it is as it was. A value its class no longer has, or
    /// that no longer fits, is warned about and left. One that cannot be made is an error, and the entity goes without
    /// it. Main thread.
    /// </summary>
    void Restore(Entity entity, Script script, ScriptState state);
}

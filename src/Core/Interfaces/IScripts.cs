using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The scripts (<see cref="IScript"/>): makes them, and runs a copy of each an entity's
/// <see cref="Contexts.Components.Scripts"/> names, made for that entity with the values a prefab gives it, until the
/// entity no longer names it or is destroyed. An <see cref="IBehavior"/> has its hooks called each frame, less often
/// far from the player; a script that is also a system, of any kind, is added to the <see cref="IEcs"/>.
/// </summary>
public interface IScripts
{
    /// <summary>
    /// A new script of the class <paramref name="script"/> declares that runs on no entity, as a pipeline script does:
    /// its constructor given the services it asks for, its properties at their defaults; why not, when it has other
    /// than one public constructor, asks for an entity or for what nothing provides, or throws. Any thread: a
    /// constructor only keeps what it is given.
    /// </summary>
    Result<IScript> Make(Script script);

    /// <summary>
    /// Gives <paramref name="entity"/> its own copy of <paramref name="script"/>, as a part's component is copied to
    /// each entity spawned from it: its script joins the entity's <see cref="Contexts.Components.Scripts"/> when it is
    /// not there, and in the next frame, once whatever spawned with the entity is all there, a copy is made for the
    /// entity, its constructor given it first and then the services it asks for, given the script's values, started
    /// (<see cref="IScript.Start"/>) and run until the entity no longer names it. Not while a domain loads in place of
    /// the world (<see cref="World.IsLoading"/>), unless the class is <see cref="Attributes.ImpatientAttribute"/>. A
    /// script that game code adds to the component itself starts the same way, with the script's defaults. One that
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
    /// Puts the script <paramref name="state"/> names back on <paramref name="entity"/> as it saved it
    /// (<see cref="Save"/>): its class found in the code loaded now, as reading its file finds it, a copy made for the
    /// entity as <see cref="Attach"/> makes one, its values and fields set back, and run until the entity is destroyed,
    /// but not started, as it is as it was. A value its class no longer has, or that no longer fits, is warned about
    /// and left. One whose class is not found, or that cannot be made, is an error, and the entity goes without it.
    /// Main thread, in the frame the world is put back.
    /// </summary>
    void Restore(Entity entity, ScriptState state);
}

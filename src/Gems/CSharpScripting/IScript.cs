using Magic.Assets;
using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.CSharpScripting;

/// <summary>
/// What an entity does, in C#: a class with the name of its <c>.cs</c> file, which is a <see cref="Script"/> asset.
/// </summary>
/// <remarks>
/// <para>
/// An entity has a script when its <see cref="ScriptContainer"/> component holds the handle of the script. Each entity gets
/// its own copy of the script. The copy ends when the handle goes from the component or the entity is destroyed. A
/// copy that is <see cref="IDisposable"/> is disposed then.
/// </para>
/// <para>
/// The constructor takes the <see cref="Entity"/> first, if it uses it, then the services that it needs. It only keeps
/// them: <c>Spin(Entity owner, World world)</c>. Do the first work in <see cref="Start"/>, and in
/// <see cref="Resume"/> for a copy that a save made.
/// </para>
/// </remarks>
public interface IScript
{
    /// <summary>
    /// Starts the copy on its entity, in the first frame that it runs. Game thread.
    /// </summary>
    void Start() { }

    /// <summary>
    /// Takes a copy that a save made up on its entity, in place of <see cref="Start"/>, in the first frame that it
    /// runs. Game thread.
    /// </summary>
    /// <remarks>
    /// Make here what the save does not hold and <see cref="Start"/> would have made: a subscription to an event, a
    /// delegate, a task, and the first work with a service that the constructor kept. The fields that the save holds
    /// are set before this call, so the copy goes on from the state that it had.
    /// </remarks>
    void Resume() { }
}

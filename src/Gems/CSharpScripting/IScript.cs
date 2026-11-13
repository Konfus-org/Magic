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
/// them: <c>Spin(Entity owner, World world)</c>. Do the first work in <see cref="Start"/>.
/// </para>
/// </remarks>
public interface IScript
{
    /// <summary>
    /// Starts the copy on its entity, in the first frame that it runs. Game thread.
    /// </summary>
    void Start() { }
}

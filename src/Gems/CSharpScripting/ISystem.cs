using Magic.Ecs;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.CSharpScripting;

/// <summary>
/// A script that does work over many entities each frame.
/// </summary>
/// <remarks>
/// Give an entity of its own the system, as any script. The system runs while that entity has it. It runs in
/// <c>Update</c> on the game thread, each frame, wherever its entity is. If it throws, the error is logged and the
/// system ends.
/// </remarks>
public interface ISystem : IScript
{
    /// <summary>
    /// Does one frame of the system's work over the entities of <paramref name="world"/>.
    /// </summary>
    void Run(in Frame frame, World world);
}

/// <summary>
/// A system over the entities that have a <typeparamref name="T1"/>, which it can change.
/// </summary>
public interface ISystem<T1> : ISystem
    where T1 : unmanaged, IComponent
{
    /// <summary>
    /// Does one frame of the system's work over <paramref name="entities"/>.
    /// </summary>
    void Run(in Frame frame, EcsMutation<T1> entities);

    void ISystem.Run(in Frame frame, World world) => Run(frame, world.Mutate<T1>());
}

/// <summary>
/// A system over the entities that have both a <typeparamref name="T1"/> and a <typeparamref name="T2"/>, which it can
/// change.
/// </summary>
public interface ISystem<T1, T2> : ISystem
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    /// <summary>
    /// Does one frame of the system's work over <paramref name="entities"/>.
    /// </summary>
    void Run(in Frame frame, EcsMutation<T1, T2> entities);

    void ISystem.Run(in Frame frame, World world) => Run(frame, world.Mutate<T1, T2>());
}

using Magic.Contexts.ECS;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// An asset that can be put in the world (<see cref="World.Spawn"/>): it says how it becomes entities, as an
/// <see cref="IJsonAsset{TSelf}"/> says how its file is read.
/// </summary>
public interface ISpawnable
{
    /// <summary>
    /// Makes its entities in <paramref name="world"/>, placed at <paramref name="at"/>, and hands them back. Main
    /// thread.
    /// </summary>
    Entity[] Spawn(World world, Transform at);
}

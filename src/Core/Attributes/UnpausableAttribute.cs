using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Attributes;

/// <summary>
/// Marks a script (<see cref="IScript"/>) or a system (<see cref="ISystem"/>, <see cref="IParallelSystem"/>) that runs
/// while the world is paused (<see cref="World.IsPaused"/>): a camera that flies, a pause menu, or what copies the world
/// for drawing it. Without it, a behaviour's Update and LateUpdate and a system wait while the world is paused, unless
/// the behaviour's entity is tagged <see cref="Tag.Unpausable"/>, which marks one entity rather than a class. There
/// are no fixed steps while the world is paused, so nothing's FixedUpdate runs then, with it or without. What moves
/// while the world is paused moves by <see cref="Frame.RealDeltaSeconds"/>, as the world's time stands still.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnpausableAttribute : Attribute;

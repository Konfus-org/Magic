using Magic.Attributes.Scripts;
using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// A script that is an ECS system: added to the <see cref="Services.Scheduler"/>, it is scheduled on the
/// <see cref="IEcs"/> and <see cref="Run"/> once every time its phase does, after the systems added before it. Its
/// phase is the one its class is marked with by <see cref="PhaseAttribute"/>, <see cref="UpdateType.Update"/> when
/// it is not marked. What it iterates is its own business: a system that wants a query builds it from the
/// <see cref="IEcs"/> it was constructed with.
/// </summary>
public interface ISystem : IScript
{
    /// <summary>
    /// One run, with the frame it is part of; in <see cref="UpdateType.FixedUpdate"/> its delta is the fixed step.
    /// </summary>
    void Run(in Frame frame);
}

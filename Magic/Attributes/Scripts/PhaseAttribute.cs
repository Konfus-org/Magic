using Magic.Interfaces;

namespace Magic.Attributes.Scripts;

/// <summary>
/// Marks an <see cref="ISystem"/> with the phase of the frame it runs in. A system that is not marked runs in
/// <see cref="UpdateType.Update"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PhaseAttribute(UpdateType phase) : Attribute
{
    public UpdateType Phase { get; } = phase;
}

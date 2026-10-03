namespace Magic.Attributes;

/// <summary>
/// Marks an <see cref="Interfaces.IBehavior"/> whose Update and LateUpdate must be called every frame however far
/// its entity is from the cameras. Without it a distant behaviour is called less often, with the time that passed
/// since its last call as the frame's delta: fine for what moves or counts time, wrong for what must see every frame.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AlwaysUpdateAttribute : Attribute
{
}

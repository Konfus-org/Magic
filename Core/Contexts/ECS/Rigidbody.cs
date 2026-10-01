namespace Magic.Contexts.Components;

/// <summary>
/// Moved by physics rather than by hand. A <see cref="Kinematic"/> body is driven by its transform and pushes
/// others without being pushed. <see cref="Mass"/> is in kilograms.
/// </summary>
public struct Rigidbody : IComponent
{
    public float Mass { get; set; }

    public bool Kinematic { get; set; }
}

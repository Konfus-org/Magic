using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.Ecs.Components;

/// <summary>
/// Where a <see cref="Contact"/> is in its life; <see cref="None"/> is an empty slot.
/// </summary>
public enum ContactPhase : byte
{
    None,

    /// <summary>
    /// The two began to touch this step.
    /// </summary>
    Enter,

    /// <summary>
    /// Still touching, after the step they began to.
    /// </summary>
    Stay,

    /// <summary>
    /// They parted this step: held for this one step, then gone.
    /// </summary>
    Exit,
}

/// <summary>
/// The bodies that the body of an entity touches, for gameplay.
/// </summary>
/// <remarks>
/// <para>
/// An entity with a <see cref="Physics"/> and this component gets, after each step, one entry for each body that
/// touches it. The entries are inline, so the component stays plain unmanaged data. It holds up to
/// <see cref="Capacity"/> entries and drops the rest.
/// </para>
/// <para>
/// An entry is <see cref="ContactPhase.Enter"/> in the step that the two begin to touch, and
/// <see cref="ContactPhase.Stay"/> in each step after while they touch. It is <see cref="ContactPhase.Exit"/> for the
/// one step after they part, with the last point and normal. Then it goes.
/// </para>
/// <para>
/// A file adds it to an entity with <c>"Contacts": {}</c>. The entries are those of one step, so a saved world holds
/// none. Read it in the Tick of a gem, where it holds what the last step found.
/// </para>
/// </remarks>
[InlineArray(Capacity)]
public struct Contacts : IComponent, IEquatable<Contacts>
{
    public const int Capacity = 8;

    private Contact _first;

    public static bool operator ==(Contacts left, Contacts right) => left.Equals(right);

    public static bool operator !=(Contacts left, Contacts right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same entries in the same slots.
    /// </summary>
    public readonly bool Equals(Contacts other) => ((ReadOnlySpan<Contact>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is Contacts other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        foreach (Contact held in this)
        {
            hash.Add(held);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// One body that touches another: the other entity, the point, the normal, the impulse and the phase of the touch.
/// </summary>
/// <remarks>
/// The point and the normal are in the world, and the normal points away from the other body. The impulse is the one
/// that the step applied along the normal, in newton-seconds.
/// </remarks>
public record struct Contact
{
    public Entity Other;

    public Vector3 Point;

    public Vector3 Normal;

    /// <summary>
    /// Newton-seconds along the normal this step: a gauge of how hard the hit was, and 0 once the two have parted.
    /// </summary>
    public float Impulse;

    public ContactPhase Phase;
}

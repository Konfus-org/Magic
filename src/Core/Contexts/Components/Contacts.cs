using System.Numerics;
using System.Runtime.CompilerServices;
using Magic.Contexts.ECS;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// What an entity's body touched, for gameplay: an entity with a <see cref="Physics"/> and this hears, after every
/// step, each body in contact with it as an entry, inline so the component stays plain unmanaged data, up to
/// <see cref="Capacity"/> of them and the rest dropped. An entry is <see cref="ContactPhase.Enter"/> the step the two
/// begin to touch, <see cref="ContactPhase.Stay"/> each step after while they do, and <see cref="ContactPhase.Exit"/>
/// for the one step after they part, with the point and normal as last seen; then it goes. A file opts an entity in
/// with <c>"Contacts": {}</c>; the entries are the step's, so a saved world holds none. Read it in Update, where it
/// holds what the last step found.
/// </summary>
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
    public readonly bool Equals(Contacts other)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (this[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

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
/// One body touching another: the other entity, the point and the normal, pointing away from the other body, in the
/// world, the impulse the step applied along the normal, in newton-seconds, and which phase the touch is in.
/// </summary>
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

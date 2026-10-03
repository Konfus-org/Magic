using System.Numerics;

namespace Magic.Contexts.Components;

public enum ColliderShape : byte
{
    /// <summary>
    /// Centred on the entity, <see cref="Collider.Radius"/>.
    /// </summary>
    Sphere,

    /// <summary>
    /// Along the local Y axis, <see cref="Collider.Radius"/> and <see cref="Collider.Height"/> end to end, caps included.
    /// </summary>
    Capsule,

    /// <summary>
    /// Centred on the entity, <see cref="Collider.HalfExtents"/> per axis.
    /// </summary>
    Cube,

    /// <summary>
    /// The entity's own <see cref="Renderer"/> model, every mesh of it.
    /// </summary>
    Mesh
}

/// <summary>
/// A collision shape on the entity; only the fields its <see cref="Shape"/> names matter. Build one with the factory methods.
/// </summary>
public struct Collider : IComponent
{
    public ColliderShape Shape { get; set; }

    public float Radius { get; set; }

    public float Height { get; set; }

    public Vector3 HalfExtents { get; set; }

    public static Collider Sphere(float radius) => new() { Shape = ColliderShape.Sphere, Radius = radius };

    public static Collider Capsule(float radius, float height) => new() { Shape = ColliderShape.Capsule, Radius = radius, Height = height };

    public static Collider Cube(Vector3 halfExtents) => new() { Shape = ColliderShape.Cube, HalfExtents = halfExtents };

    public static Collider Mesh() => new() { Shape = ColliderShape.Mesh };
}

using Magic.Contexts.Assets;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Components;

/// <summary>Which member of <see cref="ColliderData"/> a <see cref="Physics"/> component carries.</summary>
public enum ColliderShape
{
    Sphere,
    Capsule,
    Cube,
    Mesh
}

/// <summary>A collider: its shape, and the data that shape needs. Build one with the factory methods.</summary>
public struct Physics
{
    public ColliderShape Shape { get; set; }
    public ColliderData Data { get; set; }

    public static Physics Sphere(float radius)
    {
        return new() { Shape = ColliderShape.Sphere, Data = new ColliderData { Sphere = new SphereCollider { Radius = radius } } };
    }

    public static Physics Capsule(float radius, float height)
    {
        return new() { Shape = ColliderShape.Capsule, Data = new ColliderData { Capsule = new CapsuleCollider { Radius = radius, Height = height } } };
    }

    public static Physics Cube(Vector3 halfExtents)
    {
        return new() { Shape = ColliderShape.Cube, Data = new ColliderData { Cube = new CubeCollider { HalfExtents = halfExtents } } };
    }

    public static Physics Mesh(Handle<Model> model, int meshIndex)
    {
        return new() { Shape = ColliderShape.Mesh, Data = new ColliderData { Mesh = new MeshCollider { Model = model, MeshIndex = meshIndex } } };
    }
}

/// <summary>
/// Data for one collider shape. All variants share the same memory (a union), so read only the one that
/// matches <see cref="Physics.Shape"/>.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct ColliderData
{
    [FieldOffset(0)] public SphereCollider Sphere;
    [FieldOffset(0)] public CapsuleCollider Capsule;
    [FieldOffset(0)] public CubeCollider Cube;
    [FieldOffset(0)] public MeshCollider Mesh;
}

/// <summary>Sphere centred on the entity.</summary>
public struct SphereCollider
{
    public float Radius { get; set; }
}

/// <summary>Capsule along the local Y axis; <see cref="Height"/> is end to end, caps included.</summary>
public struct CapsuleCollider
{
    public float Radius { get; set; }
    public float Height { get; set; }
}

/// <summary>Box centred on the entity; <see cref="HalfExtents"/> is half its size per axis.</summary>
public struct CubeCollider
{
    public Vector3 HalfExtents { get; set; }
}

/// <summary>Collides with one mesh of a model: meshes are not assets of their own, so a model plus an index into <see cref="Model.Meshes"/> names it.</summary>
public struct MeshCollider
{
    public Handle<Model> Model { get; set; }
    public int MeshIndex { get; set; }
}

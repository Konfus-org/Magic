using System.Numerics;
using Magic.Assets;

namespace Magic.DeferredRendering.World;

/// <summary>
/// The cube that a model that failed draws in its place: one metre a side about its origin, its faces flat and wound
/// outward. It is made in code, so that a failure reads nothing from disk. It is in the model table under
/// <see cref="Handle"/>, which is no asset's, and a gem that makes its own data of a model makes it from
/// <see cref="Model"/> for that handle.
/// </summary>
public static class FailureCube
{
    public static Handle<Model> Handle { get; } = new(ulong.MaxValue);

    public static Model Model { get; } = Build();

    private static Model Build()
    {
        ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        ReadOnlySpan<Vector2> corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        Vertex[] vertices = new Vertex[24];
        uint[] indices = new uint[36];
        for (int face = 0; face < 6; face++)
        {
            // The face's normal, and u and v across it with u x v the normal, so its two triangles wind outward.
            float sign = face < 3 ? 1 : -1;
            Vector3 normal = axes[face % 3] * sign;
            Vector3 u = axes[(face + 1) % 3];
            Vector3 v = axes[(face + 2) % 3] * sign;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 at = corners[corner];
                vertices[(face * 4) + corner] = new Vertex
                {
                    Position = (normal + (at.X * u) + (at.Y * v)) * 0.5f,
                    Normal = normal,
                    Tangent = new Vector4(u, 1),
                    Uv = (at + Vector2.One) * 0.5f,
                };
            }

            uint first = (uint)face * 4;
            ReadOnlySpan<uint> triangles = [first, first + 1, first + 2, first, first + 2, first + 3];
            triangles.CopyTo(indices.AsSpan(face * 6));
        }

        return new Model { Meshes = [new Mesh { Vertices = vertices, Indices = indices }] };
    }
}

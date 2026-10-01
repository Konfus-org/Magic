using Magic.Extensions;
using System.Numerics;

namespace Magic.Mathematics;

/// <summary>A sphere; what the GPU culls against. The default has radius 0, which means "unknown".</summary>
public readonly record struct BoundingSphere(Vector3 Center, float Radius)
{
    /// <summary>
    /// The centre transformed; the radius scaled by the longest axis of the matrix, which is conservative
    /// for non-uniform scale.
    /// </summary>
    public BoundingSphere Transform(in Matrix4x4 m)
    {
        return new BoundingSphere(Vector3.Transform(Center, m), Radius * m.MaxScale);
    }
}

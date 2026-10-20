using Magic.Contexts.Components;

namespace Magic.Box3DPhysics;

/// <summary>
/// What each <see cref="Surface"/> is to Box3D: a friction coefficient, from ice's to glue's, and for the stickiest a
/// little rolling resistance, so a sticky ball stops rolling. Two surfaces that touch take the geometric mean of
/// their frictions and the larger of their bounces, so a sticky box on ice still slides, a little.
/// </summary>
internal static class Surfaces
{
    public static B3.SurfaceMaterial Material(Surface surface, float bounce)
    {
        B3.SurfaceMaterial material = B3.DefaultSurfaceMaterial();
        (material.Friction, material.RollingResistance) = surface switch
        {
            Surface.Slippery => (0.05f, 0f),
            Surface.Slick => (0.2f, 0f),
            Surface.Grippy => (0.9f, 0.01f),
            Surface.Sticky => (1.5f, 0.05f),
            Surface.Normal => (0.6f, 0f),
            _ => (0.6f, 0f),
        };
        material.Restitution = Math.Clamp(bounce, 0, 1);
        return material;
    }
}

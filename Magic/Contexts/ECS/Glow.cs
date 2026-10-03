using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>
/// An unlit bright dot at its entity's position, always facing the camera: what a light source looks like from far
/// away, where the dot is all there is to see of it. It lights nothing and costs a few pixels, so there can be
/// thousands. <see cref="Color"/> is linear, and above 1 it still reads as bright after the tonemap;
/// <see cref="Radius"/> is in metres, and however far away it is, it stays a pixel or two on screen. A far chunk's
/// stand-in has one for each of the chunk's lights.
/// </summary>
public struct Glow : IComponent
{
    public Glow(Vector3 color, float radius)
    {
        Color = color;
        Radius = radius;
    }

    public Vector3 Color { get; set; }

    public float Radius { get; set; }
}

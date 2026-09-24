using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Components;

/// <summary>Which member of <see cref="LightData"/> a <see cref="Light"/> carries.</summary>
public enum LightType : byte
{
    Directional,
    Point,
    Spot
}

/// <summary>
/// A light: its type, the values every type shares, and the data that type needs. One component rather than
/// one per type, like <see cref="Physics"/> and <see cref="ColliderData"/>, so a renderer iterates one query
/// and a scene switches a light's type without swapping components. Build one with the factory methods.
/// <see cref="Color"/> is linear and <see cref="Intensity"/> scales it. A directional light shines along its
/// entity's +Z; the others radiate from its position (a spot along +Z too).
/// </summary>
public struct Light
{
    public LightType Type { get; set; }
    public LightData Data { get; set; }

    /// <summary>Light from infinitely far away (the sun). Only one directional light may cast shadows.</summary>
    public static Light Directional(Vector3 color, float intensity, bool castsShadows = true)
    {
        return new()
        {
            Type = LightType.Directional,
            Data = new LightData
            {
                Directional = new DirectionalLightData { Color = color, Intensity = intensity, CastsShadows = castsShadows }
            }
        };
    }

    /// <summary>Nothing beyond <paramref name="range"/> metres is lit, so the renderer can cull it.</summary>
    public static Light Point(Vector3 color, float intensity, float range)
    {
        return new() { Type = LightType.Point, Data = new LightData { Point = new PointLightData { Color = color, Intensity = intensity, Range = range } } };
    }

    /// <summary>
    /// A cone along +Z: full intensity inside <paramref name="innerDegrees"/> (the full cone angle), falling
    /// to nothing at <paramref name="outerDegrees"/>. Degrees here because that is how people write them;
    /// the component stores radians because that is what the shader wants.
    /// </summary>
    public static Light Spot(Vector3 color, float intensity, float range, float innerDegrees, float outerDegrees)
    {
        return new()
        {
            Type = LightType.Spot,
            Data = new LightData
            {
                Spot = new SpotLightData
                {
                    Color = color,
                    Intensity = intensity,
                    Range = range,
                    InnerAngle = float.DegreesToRadians(innerDegrees),
                    OuterAngle = float.DegreesToRadians(outerDegrees),
                },
            },
        };
    }
}

/// <summary>
/// Data for one light type. All variants share the same memory (a union), so read only the one that
/// matches <see cref="Light.Type"/>. A directional light has none.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct LightData
{
    [FieldOffset(0)] public DirectionalLightData Directional;
    [FieldOffset(0)] public PointLightData Point;
    [FieldOffset(0)] public SpotLightData Spot;
}

public struct DirectionalLightData
{
    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public bool CastsShadows { get; set; }
}

public struct PointLightData
{
    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public bool CastsShadows { get; set; }

    /// <summary>Metres; nothing beyond it is lit.</summary>
    public float Range { get; set; }
}

public struct SpotLightData
{
    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public bool CastsShadows { get; set; }

    /// <summary>Metres; nothing beyond it is lit.</summary>
    public float Range { get; set; }

    /// <summary>Full cone angle in radians inside which the light is at full intensity.</summary>
    public float InnerAngle { get; set; }

    /// <summary>Full cone angle in radians beyond which there is no light.</summary>
    public float OuterAngle { get; set; }
}

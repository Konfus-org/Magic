using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Extensions;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// A camera to draw a frame from: the component (which says the <see cref="RenderTarget"/>) and its world matrix.
/// </summary>
internal readonly record struct View(Camera Camera, Matrix4x4 World);

internal enum LightKind : byte
{
    Directional,
    Point,
    Spot,
    Area
}

/// <summary>
/// One light of any kind with its entity's world matrix; the fields a kind does not have are zero. Size is an area
/// light's width and height.
/// </summary>
internal readonly record struct LightInstance(LightKind Kind, Vector3 Color, float Intensity, float Range, float InnerAngle, float OuterAngle, Vector2 Size, bool CastsShadows, Matrix4x4 World)
{
    public static LightInstance Of(in DirectionalLight light, Matrix4x4 world)
    {
        return new(LightKind.Directional, light.Color.Rgb, light.Intensity, 0f, 0f, 0f, Vector2.Zero, light.CastsShadows, world);
    }

    public static LightInstance Of(in PointLight light, Matrix4x4 world)
    {
        return new(LightKind.Point, light.Color.Rgb, light.Intensity, light.Range, 0f, 0f, Vector2.Zero, light.CastsShadows, world);
    }

    public static LightInstance Of(in SpotLight light, Matrix4x4 world)
    {
        return new(LightKind.Spot, light.Color.Rgb, light.Intensity, light.Range, light.InnerAngle, light.OuterAngle, Vector2.Zero, light.CastsShadows, world);
    }

    public static LightInstance Of(in AreaLight light, Matrix4x4 world)
    {
        return new(LightKind.Area, light.Color.Rgb, light.Intensity, light.Range, 0f, 0f, new Vector2(light.Width, light.Height), light.CastsShadows, world);
    }
}

/// <summary>
/// How a light component of one kind becomes a <see cref="LightInstance"/>: one of the <c>LightInstance.Of</c> overloads.
/// </summary>
internal delegate LightInstance LightOf<T>(in T light, Matrix4x4 world);

/// <summary>
/// Everything drawing one entity takes; <see cref="Static"/> promises the world matrix never changes, and one that
/// is <see cref="Hidden"/> is registered without being drawn.
/// </summary>
internal readonly record struct InstanceDesc(Handle<Model> Model, MaterialSlots Materials, RenderFlags Flags, float CullRadius, Matrix4x4 World, bool Static, bool Hidden);

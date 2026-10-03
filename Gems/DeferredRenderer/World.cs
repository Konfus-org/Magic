using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
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
    Spot
}

/// <summary>
/// One light of any kind with its entity's world matrix; the fields a kind does not have are zero.
/// </summary>
internal readonly record struct LightInstance(LightKind Kind, Vector3 Color, float Intensity, float Range, float InnerAngle, float OuterAngle, bool CastsShadows, Matrix4x4 World);

/// <summary>
/// Everything drawing one entity takes; <see cref="Static"/> promises the world matrix never changes, and one that
/// is <see cref="Hidden"/> is registered without being drawn.
/// </summary>
internal readonly record struct InstanceDesc(Handle<Model> Model, MaterialSlots Materials, RenderFlags Flags, float CullRadius, Matrix4x4 World, bool Static, bool Hidden);

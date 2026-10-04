using Magic.Contexts.Components;
using Magic.Extensions;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// One face of a local light's shadow: a perspective view from the light, one for a spot, six for a point.
/// </summary>
internal readonly record struct ShadowFace(Vector3 Eye, Matrix4x4 Rotation, Matrix4x4 Projection, float FovYRadians)
{
    /// <summary>
    /// The face's view-projection for positions relative to <paramref name="origin"/>: the light itself for its shadow
    /// view, the camera for the lighting that reads it.
    /// </summary>
    public Matrix4x4 ViewProj(Vector3 origin)
    {
        return Matrix4x4.CreateTranslation(origin - Eye) * Rotation * Projection;
    }
}

/// <summary>
/// The spot and point lights' shadow views, as maths: a spot looks along its cone, a point light looks down each world
/// axis both ways with a right-angle cone, slightly wider so the filter's taps at a face's edge land inside it.
/// </summary>
internal static class LocalShadows
{
    public const int PointFaces = 6;

    private const float PointFaceFovDegrees = 91.5f;

    private const float SpotMarginDegrees = 4f;

    /// <summary>
    /// How near a light's shadow view starts, in metres.
    /// </summary>
    public const float Near = 0.05f;

    public static ShadowFace Spot(in LightInstance light)
    {
        float fov = float.DegreesToRadians(Math.Clamp(float.RadiansToDegrees(light.OuterAngle) + SpotMarginDegrees, 1f, 170f));
        return new ShadowFace(light.World.Translation, Camera.ViewMatrix(light.World), Matrix4x4.PerspectiveReverseZ(fov, 1f, Near, MathF.Max(light.Range, Near + 0.01f)), fov);
    }

    /// <summary>
    /// Face <paramref name="face"/> of a point light: +X, -X, +Y, -Y, +Z, -Z, the order <c>Shadows.hlsli</c> picks them in.
    /// </summary>
    public static ShadowFace Point(in LightInstance light, int face)
    {
        Vector3 forward = face switch
        {
            0 => Vector3.UnitX,
            1 => -Vector3.UnitX,
            2 => Vector3.UnitY,
            3 => -Vector3.UnitY,
            4 => Vector3.UnitZ,
            _ => -Vector3.UnitZ,
        };
        float fov = float.DegreesToRadians(PointFaceFovDegrees);
        return new ShadowFace(light.World.Translation, Cascades.LightRotation(forward), Matrix4x4.PerspectiveReverseZ(fov, 1f, Near, MathF.Max(light.Range, Near + 0.01f)), fov);
    }

    /// <summary>
    /// How big a light's reach is on the camera's screen, as a share of the view's height: what the lights holding pages
    /// are ranked by, the biggest first. A light the camera is inside counts as filling the view.
    /// </summary>
    public static float Score(in LightInstance light, Vector3 cameraPos, float projScaleY)
    {
        float distance = Vector3.Distance(light.World.Translation, cameraPos);
        return distance <= light.Range ? 1f : light.Range * projScaleY / distance;
    }
}

using System.Drawing;
using System.Numerics;

namespace DebugToolsGem;

/// <summary>
/// One camera's view of the main window, as the debug UI needs it to put text at a world position: the camera-relative
/// view-projection, where the camera is, and the view's rectangle in window pixels.
/// </summary>
internal readonly record struct View(Matrix4x4 ViewProjection, Vector3 Camera, RectangleF Pixels)
{
    /// <summary>
    /// The pixel <paramref name="position"/> shows at; false when it is behind the camera, outside the view or
    /// farther than <paramref name="maxDistance"/> metres from the camera.
    /// </summary>
    public bool Project(Vector3 position, float maxDistance, out Vector2 pixel)
    {
        pixel = default;
        Vector3 relative = position - Camera;
        if (relative.LengthSquared() > maxDistance * maxDistance)
            return false;

        Vector4 clip = Vector4.Transform(new Vector4(relative, 1f), ViewProjection);
        if (clip.W <= 0f)
            return false;

        Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
        if (MathF.Abs(ndc.X) > 1f || MathF.Abs(ndc.Y) > 1f)
            return false;

        pixel = new Vector2(Pixels.X + (((ndc.X * 0.5f) + 0.5f) * Pixels.Width), Pixels.Y + ((0.5f - (ndc.Y * 0.5f)) * Pixels.Height));
        return true;
    }
}

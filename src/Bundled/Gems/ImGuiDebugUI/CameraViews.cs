using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Extensions;
using Magic.Interfaces;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// One camera's view of the window, as text in the world needs it: where the camera is, the turn that takes the world
/// into its frame, the tangents of half its field of view across and up, how near it sees, and its area of the window
/// in pixels.
/// </summary>
internal readonly record struct CameraView(
    Vector3 Position,
    Quaternion Inverse,
    Vector2 Tangents,
    float Near,
    Rectangle Area)
{
    /// <summary>
    /// The pixel <paramref name="point"/> shows at; false when it is behind the camera, outside the view or farther
    /// than <paramref name="distance"/> metres from the camera.
    /// </summary>
    internal bool TryProject(Vector3 point, float distance, out Vector2 pixel)
    {
        pixel = default;
        Vector3 relative = point - Position;

        if (relative.LengthSquared() > distance * distance) return false;

        // The camera looks along its +Z, +Y up.
        Vector3 local = Vector3.Transform(relative, Inverse);

        if (local.Z <= Near) return false;

        Vector2 ndc = new(local.X / (local.Z * Tangents.X), local.Y / (local.Z * Tangents.Y));

        if (MathF.Abs(ndc.X) > 1 || MathF.Abs(ndc.Y) > 1) return false;

        pixel = new Vector2(
            Area.X + (((ndc.X * 0.5f) + 0.5f) * Area.Width),
            Area.Y + ((0.5f - (ndc.Y * 0.5f)) * Area.Height));

        return true;
    }
}

/// <summary>
/// The views the window's cameras show, copied in LateUpdate, before the gem's own LateUpdate puts text in the world
/// where they show it. A camera that draws into a render texture shows nothing in the window, so it has no view here.
/// </summary>
internal sealed class CameraViews(IWindow window) : ISystem<Transform, Camera>
{
    public Phase Phase => Phase.LateUpdate;

    internal List<CameraView> Views { get; } = [];

    public void Run(in Frame frame, EcsQuery<Transform, Camera> query)
    {
        Views.Clear();
        Size size = new(window.Width, window.Height);
        foreach ((_, Span<Transform> transforms, Span<Camera> cameras) in query)
        {
            for (int i = 0; i < cameras.Length; i++)
            {
                Rectangle area = cameras[i].Target.Section.AreaIn(size);

                if (!cameras[i].Target.IsWindow || area.Width <= 0 || area.Height <= 0) continue;

                // As the renderer frames a view: the field of view is vertical, and the width follows the area's shape.
                float tanY = MathF.Tan(cameras[i].FieldOfView * (MathF.PI / 360));
                Vector2 tangents = new(tanY * area.Width / area.Height, tanY);
                Views.Add(new CameraView(
                    transforms[i].Position,
                    Quaternion.Conjugate(transforms[i].Rotation),
                    tangents,
                    cameras[i].Near,
                    area));
            }
        }
    }
}

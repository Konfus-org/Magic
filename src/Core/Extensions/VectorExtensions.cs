using System.Numerics;
using Magic.Contexts.Components;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace Magic.Extensions;

/// <summary>
/// Where a point on the screen is in the world, so picking and aiming go through one conversion that matches how a
/// camera is drawn.
/// </summary>
public static class VectorExtensions
{
    extension(Vector2 screen)
    {
        /// <summary>
        /// The world point under this pixel, <paramref name="depth"/> metres in front of the camera, which looks along
        /// its +Z through the middle of the area it draws into: the pointer's ray runs from the camera's position to
        /// <c>pointer.ToWorld(camera, transform, size, far)</c>. <paramref name="size"/> is what the camera draws
        /// into, the window's or its render texture's; a window camera's section of it is allowed for.
        /// </summary>
        public Vector3 ToWorld(in Camera camera, in Transform transform, Size size, float depth)
        {
            // The pixel as a direction from the camera at a depth of 1: its place across the area the camera draws
            // into, -1 to 1 each way with +Y up, times the tangent of half the field of view along each axis.
            Rectangle area = camera.Target.IsWindow
                ? camera.Target.Section.GetAreaIn(size)
                : new Rectangle(Point.Empty, size);
            float tanY = MathF.Tan(camera.FieldOfView * (MathF.PI / 360));
            float tanX = tanY * area.Width / area.Height;
            float x = (((screen.X - area.X) / area.Width * 2) - 1) * tanX;
            float y = (1 - ((screen.Y - area.Y) / area.Height * 2)) * tanY;

            return transform.Position + Vector3.Transform(new Vector3(x, y, 1) * depth, transform.Rotation);
        }
    }
}

using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Fallbacks;

/// <summary>
/// Swings its camera slowly around the world's up axis through the origin, keeping its height, distance and tilt,
/// so the four failure cubes are seen from every side while they breathe. A turn every 36 seconds is slow
/// enough that the row stays in frame for a long look at each.
/// </summary>
internal sealed class OrbitCamera(Handle entity, IEcs ecs) : IBehavior
{
    /// <summary>Degrees per second; the chunk sets it beside the script's id.</summary>
    public float DegreesPerSecond { get; set; } = 20f;

    public void Update(in Frame frame)
    {
        ref Transform transform = ref ecs.Get<Transform>(entity);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * frame.Delta));

        // A chunk that gives no rotation leaves the all-zero quaternion, which draws as identity but would stay zero here.
        Quaternion tilt = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;

        transform.Position = Vector3.Transform(transform.Position, yaw);
        transform.Rotation = Quaternion.Concatenate(tilt, yaw); // its own tilt first, then the turn
    }
}

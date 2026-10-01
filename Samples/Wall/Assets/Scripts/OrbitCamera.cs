using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Wall;

/// <summary>
/// Swings its camera around the world's up axis through the origin, keeping its height, distance and tilt,
/// so the scene is seen from all sides. Rounding the wall shows the occlusion-culled count rise and fall in the Stats line.
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

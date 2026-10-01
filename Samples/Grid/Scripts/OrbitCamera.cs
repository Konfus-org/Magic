using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Grid;

/// <summary>
/// Swings every camera around the world's up axis through the origin, keeping its height, distance and tilt,
/// so the scene is seen from all sides. Slow: at this radius a degree is several metres, and the streaming system keeps up with the cameras.
/// </summary>
internal sealed class OrbitCamera(IEcs ecs) : IGem
{
    /// <summary>Degrees per second.</summary>
    private const float DegreesPerSecond = 4f;

    private readonly IEcsQuery<Transform, Camera> _cameras = ecs.Query<Transform, Camera>().Build();

    public void Dispose()
    {
        _cameras.Dispose();
    }

    public void Update(in Frame frame)
    {
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        _cameras.Each((Handle _, ref Transform transform, ref Camera _) => Orbit(ref transform, yaw));
    }

    private static void Orbit(ref Transform transform, Quaternion yaw)
    {
        // A chunk that gives no rotation leaves the all-zero quaternion, which draws as identity but would stay zero here.
        Quaternion tilt = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;

        transform.Position = Vector3.Transform(transform.Position, yaw);
        transform.Rotation = Quaternion.Concatenate(tilt, yaw); // its own tilt first, then the turn
    }
}

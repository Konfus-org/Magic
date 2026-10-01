using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace SplitScreen;

/// <summary>
/// Swings every camera around the world's up axis through the origin, keeping its height, distance and tilt: the one in
/// the top half one way, the one in the bottom half the other way and faster, so the two halves are plainly two views.
/// </summary>
internal sealed class OrbitCamera(IEcs ecs) : IGem
{
    /// <summary>Degrees per second.</summary>
    private const float Speed = 20f;

    private readonly IEcsQuery<Transform, Camera> _cameras = ecs.Query<Transform, Camera>().Build();

    public void Update(in Frame frame)
    {
        float delta = frame.Delta;
        _cameras.Each((Handle _, ref Transform transform, ref Camera camera) =>
        {
            float speed = camera.Viewport == ViewportRegion.BottomHalf ? -2f * Speed : Speed;
            Orbit(ref transform, Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(speed * delta)));
        });
    }

    private static void Orbit(ref Transform transform, Quaternion yaw)
    {
        // A chunk that gives no rotation leaves the all-zero quaternion, which draws as identity but would stay zero here.
        Quaternion tilt = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;

        transform.Position = Vector3.Transform(transform.Position, yaw);
        transform.Rotation = Quaternion.Concatenate(tilt, yaw); // its own tilt first, then the turn
    }

    public void Dispose()
    {
        _cameras.Dispose();
    }
}

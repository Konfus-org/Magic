using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Mathematics;
using System.Numerics;

namespace SampleShared;

/// <summary>
/// Swings every camera around the world's up axis through the origin, keeping its height, distance and tilt, so a
/// scene shows all its sides, and streams, with nobody at the controls. The first press of the right mouse button,
/// which is how a <see cref="CameraController"/> is flown, ends it: the cameras are the user's from then on.
/// </summary>
internal sealed class OrbitSystem(IEcs ecs, IInput input) : ISystem
{
    private readonly IEcsQuery<Transform, Camera> _cameras = ecs.Query<Transform, Camera>().Build();
    private bool _stopped;

    /// <summary>The chunk sets it beside the script's id.</summary>
    public float DegreesPerSecond { get; set; } = 20f;

    public void Dispose()
    {
        _cameras.Dispose();
    }

    public void Run(in Frame frame)
    {
        _stopped |= input.IsDown(MouseButton.Right);
        if (_stopped)
            return;

        Quaternion yaw = Quaternion.CreateFromAxisAngle(Axis.Up, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        _cameras.Each((Handle _, ref Transform transform, ref Camera _) =>
        {
            // A chunk that gives no rotation may leave the all-zero quaternion, which draws as identity but would stay zero here.
            Quaternion tilt = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;

            transform.Position = Vector3.Transform(transform.Position, yaw);
            transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(tilt, yaw)); // its own tilt first, then the turn
        });
    }
}

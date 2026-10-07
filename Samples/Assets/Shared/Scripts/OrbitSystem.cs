using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Mathematics;
using System.Numerics;

namespace Samples.Shared;

/// <summary>
/// Swings every camera around the world's up axis through the origin, keeping its height, distance and tilt, so a scene
/// shows all its sides, and streams, with nobody at the controls. The first press of the right mouse button, which is
/// how a <see cref="CameraController"/> is flown, ends it: the cameras are the user's from then on.
/// </summary>
internal sealed class OrbitSystem : ISystem
{
    private readonly IInput _input;
    private readonly IEcsQuery<Transform, Camera> _cameras;
    private readonly QueryEachAction<Transform, Camera> _turn; // one delegate, not one per frame

    public OrbitSystem(IEcs ecs, IInput input)
    {
        _input = input;
        _cameras = ecs.Query<Transform, Camera>().Build();
        _turn = Turn;
    }

    public void Dispose()
    {
        _cameras.Dispose();
    }

    /// <summary>
    /// The chunk sets it beside the script's id.
    /// </summary>
    public float DegreesPerSecond { get; set; } = 20f;

    /// <summary>
    /// The right mouse button has been pressed: the cameras are the user's.
    /// </summary>
    private bool Stopped { get; set; }

    /// <summary>
    /// This frame's turn about the up axis, for <see cref="Turn"/>.
    /// </summary>
    private Quaternion Yaw { get; set; }

    public void Run(in Frame frame)
    {
        Stopped |= _input.IsDown(MouseButton.Right);
        if (Stopped)
            return;

        Yaw = Quaternion.CreateFromAxisAngle(Axis.Up, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        _cameras.Each(_turn);
    }

    private void Turn(Handle entity, ref Transform transform, ref Camera camera)
    {
        transform.Position = Vector3.Transform(transform.Position, Yaw);
        transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(transform.Rotation, Yaw)); // its own tilt first, then the turn
    }
}

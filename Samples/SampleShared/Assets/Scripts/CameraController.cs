using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Mathematics;
using System.Numerics;

namespace SampleShared;

/// <summary>
/// Lets the user fly its entity. While the right mouse button is held the mouse looks around, W A S D move along
/// the view, Q and E go down and up, and either shift moves faster. Nothing moves while the button is up, so typing
/// in the console or clicking a debug window flies nothing.
/// </summary>
internal sealed class CameraController(Handle entity, IEcs ecs, IInput input) : IBehavior
{
    private const float MaxPitch = 89f * MathF.PI / 180f;

    /// <summary>Metres per second; the chunk sets it beside the script's id.</summary>
    public float Speed { get; set; } = 5f;

    /// <summary>What <see cref="Speed"/> is multiplied by while shift is held.</summary>
    public float Boost { get; set; } = 4f;

    /// <summary>Degrees turned per point the mouse moves.</summary>
    public float Sensitivity { get; set; } = 0.15f;

    public void Update(in Frame frame)
    {
        if (!input.IsDown(MouseButton.Right))
            return;

        ref Transform transform = ref ecs.Get<Transform>(entity);
        Vector2 mouse = input.MouseDelta;

        // Yaw and pitch are read back from the rotation, so nothing is kept between frames and a chunk edit holds.
        Vector3 forward = Vector3.Transform(Axis.Forward, transform.Rotation);
        float yaw = MathF.Atan2(forward.X, forward.Z) + float.DegreesToRadians(mouse.X * Sensitivity);
        float pitch = MathF.Asin(Math.Clamp(-forward.Y, -1f, 1f)) + float.DegreesToRadians(mouse.Y * Sensitivity);
        pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);

        // Its own tilt first, then the turn.
        transform.Rotation = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Axis.Right, pitch), Quaternion.CreateFromAxisAngle(Axis.Up, yaw));

        bool boosted = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
        Vector3 along = Vector3.Transform(new Vector3(Pressed(Key.D) - Pressed(Key.A), 0f, Pressed(Key.W) - Pressed(Key.S)), transform.Rotation)
            + (Axis.Up * (Pressed(Key.E) - Pressed(Key.Q)));
        transform.Position += along * (Speed * (boosted ? Boost : 1f) * frame.Delta);
    }

    private float Pressed(Key key)
    {
        return input.IsDown(key) ? 1f : 0f;
    }
}

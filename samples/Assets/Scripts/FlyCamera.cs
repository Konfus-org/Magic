using System.Numerics;
using Magic.Attributes;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Flies its entity, a camera's, around from the keyboard and mouse or a gamepad: a script, a behaviour a prefab
/// attaches as <c>{ "id": 3315 }</c>. WASD or the left stick move it where it looks, E and Q or the right and left
/// triggers straight up and down, and Shift or the left shoulder <see cref="FastFactor"/> times as fast. It looks
/// around with the mouse while the right button is held, so the pointer is free otherwise, or with the right stick.
/// Until it is flown it stays where it spawned, looking the way it spawned facing. It flies while the world is paused,
/// by real time, so the paused world can be looked around.
/// </summary>
[Unpausable]
internal sealed class FlyCamera(Entity owner, World world, IInput input) : IBehavior
{
    private const float FastFactor = 4;

    /// <summary>
    /// Radians turned per pixel the mouse moves.
    /// </summary>
    private const float MouseTurn = 0.0025f;

    /// <summary>
    /// Radians a second turned with the right stick all the way over.
    /// </summary>
    private const float StickTurn = 2.5f;

    /// <summary>
    /// Just short of straight up or down, so it never looks over the top and flips.
    /// </summary>
    private const float MaxPitch = 89 * (MathF.PI / 180);

    /// <summary>
    /// Which way it looks: a turn about the world's up, right positive, then a tilt, down positive.
    /// </summary>
    private float _yaw;
    private float _pitch;

    /// <summary>
    /// Metres a second with a key held or a stick all the way over.
    /// </summary>
    public float Speed { get; set; } = 10;

    /// <summary>
    /// Starts looking the way its entity spawned facing.
    /// </summary>
    public void Start()
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, world.Get<Transform>(owner).Rotation);
        _yaw = MathF.Atan2(forward.X, forward.Z);
        _pitch = MathF.Atan2(-forward.Y, new Vector2(forward.X, forward.Z).Length());
    }

    public void Update(in Frame frame)
    {
        // The turn: the mouse's motion while its right button is held, and the right stick's push over the frame, up
        // looking up, as the tilt is down positive. The move: each pair of keys and the stick for it, added.
        Vector2 mouse = input[Control.MouseRight].IsDown
            ? new Vector2(input[Control.MouseMotionX].Value, input[Control.MouseMotionY].Value) * MouseTurn
            : Vector2.Zero;
        Vector2 turn = mouse + (new Vector2(input[Control.GamepadRightX].Value, -input[Control.GamepadRightY].Value)
            * (StickTurn * frame.RealDeltaSec));
        float right = Axis.Read(input, Control.KeyD, Control.KeyA) + input[Control.GamepadLeftX].Value;
        float forward = Axis.Read(input, Control.KeyW, Control.KeyS) + input[Control.GamepadLeftY].Value;
        float up = Axis.Read(input, Control.KeyE, Control.KeyQ)
            + Axis.Read(input, Control.GamepadRightTrigger, Control.GamepadLeftTrigger);

        _yaw += turn.X;
        _pitch = Math.Clamp(_pitch + turn.Y, -MaxPitch, MaxPitch);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0);

        // Along where it looks, and up and down the world's up; no faster for two keys, or a key and a stick, at once.
        Vector3 move = Vector3.Transform(new Vector3(right, 0, forward), rotation) + (Vector3.UnitY * up);
        if (move.LengthSquared() > 1) move = Vector3.Normalize(move);

        bool isFast = input[Control.KeyLeftShift].IsDown || input[Control.GamepadLeftShoulder].IsDown;
        ref Transform transform = ref world.Get<Transform>(owner);
        transform.Rotation = rotation;
        transform.Position += move * (Speed * (isFast ? FastFactor : 1) * frame.RealDeltaSec);
    }
}

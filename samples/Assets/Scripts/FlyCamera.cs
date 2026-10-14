using System.Numerics;
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
/// With <see cref="Cruise"/> set it also flies on by itself, straight ahead, which shows the world going past the
/// same way on every run.
/// </summary>
internal sealed class FlyCamera(World world, IInput input) : IBehavior
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
    /// How far a stick goes before it counts, so one that does not quite centre leaves it still.
    /// </summary>
    private const float DeadZone = 0.15f;

    /// <summary>
    /// Just short of straight up or down, so it never looks over the top and flips.
    /// </summary>
    private const float MaxPitch = 89 * (MathF.PI / 180);

    private readonly World _world = world;
    private readonly IInput _input = input;
    private Entity _entity;

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
    /// Metres a second it flies on by itself, where it looks, keys or not: 0, by default, for none.
    /// </summary>
    public float Cruise { get; set; }

    /// <summary>
    /// Starts looking the way <paramref name="entity"/> spawned facing.
    /// </summary>
    public void Start(Entity entity)
    {
        _entity = entity;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, _world.Get<Transform>(entity).Rotation);
        _yaw = MathF.Atan2(forward.X, forward.Z);
        _pitch = MathF.Atan2(-forward.Y, new Vector2(forward.X, forward.Z).Length());
    }

    public void Update(in Frame frame)
    {
        Vector2 mouse = _input[Control.MouseRight].IsDown
            ? new Vector2(_input[Control.MouseMotionX].Value, _input[Control.MouseMotionY].Value) * MouseTurn
            : Vector2.Zero;
        float stickTurn = StickTurn * frame.DeltaSeconds;
        _yaw += mouse.X + (Stick(Control.GamepadRightX) * stickTurn);
        _pitch = Math.Clamp(_pitch + mouse.Y - (Stick(Control.GamepadRightY) * stickTurn), -MaxPitch, MaxPitch);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0);

        // Along where it looks, and up and down the world's up; no faster for two keys, or a key and a stick, at once.
        float right = Axis(Control.KeyD, Control.KeyA) + Stick(Control.GamepadLeftX);
        float forward = Axis(Control.KeyW, Control.KeyS) + Stick(Control.GamepadLeftY);
        float up = Axis(Control.KeyE, Control.KeyQ) + Axis(Control.GamepadRightTrigger, Control.GamepadLeftTrigger);
        Vector3 move = Vector3.Transform(new Vector3(right, 0, forward), rotation) + (Vector3.UnitY * up);
        if (move.LengthSquared() > 1)
        {
            move = Vector3.Normalize(move);
        }

        bool isFast = _input[Control.KeyLeftShift].IsDown || _input[Control.GamepadLeftShoulder].IsDown;
        ref Transform transform = ref _world.Get<Transform>(_entity);
        transform.Rotation = rotation;
        transform.Position += move * (Speed * (isFast ? FastFactor : 1) * frame.DeltaSeconds);
        transform.Position += Vector3.Transform(Vector3.UnitZ, rotation) * (Cruise * frame.DeltaSeconds);
    }

    /// <summary>
    /// One control's value less another's: -1 to 1 for a pair of keys.
    /// </summary>
    private float Axis(Control positive, Control negative)
    {
        return _input[positive].Value - _input[negative].Value;
    }

    /// <summary>
    /// A stick axis, 0 inside the dead zone.
    /// </summary>
    private float Stick(Control axis)
    {
        float value = _input[axis].Value;
        return MathF.Abs(value) < DeadZone ? 0 : value;
    }
}

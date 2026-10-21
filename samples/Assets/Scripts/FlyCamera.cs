using System.Numerics;
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
/// With <see cref="Cruise"/> and <see cref="Orbit"/> set it flies by itself until it is flown, straight out to
/// <see cref="Radius"/> from the world's middle, then around it, looking at it, which shows the world going past the
/// same way on every run.
/// </summary>
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
    /// Radians a second it turns to look at the world's middle as it orbits, so it swings round rather than snaps.
    /// </summary>
    private const float TurnToMiddle = 45 * (MathF.PI / 180);

    private readonly World _world = world;
    private readonly IInput _input = input;

    /// <summary>
    /// Which way it looks: a turn about the world's up, right positive, then a tilt, down positive.
    /// </summary>
    private float _yaw;
    private float _pitch;

    /// <summary>
    /// Whether it still flies by itself: until the first move or turn of the controls, which hand it over for good.
    /// </summary>
    private bool _isFlyingItself = true;

    /// <summary>
    /// Metres a second with a key held or a stick all the way over.
    /// </summary>
    public float Speed { get; set; } = 10;

    /// <summary>
    /// Metres a second it flies on by itself, where it looks, while nearer the world's middle than
    /// <see cref="Radius"/>, until it is flown: 0, by default, for none.
    /// </summary>
    public float Cruise { get; set; }

    /// <summary>
    /// Metres from the world's up through the origin it cruises out to before it orbits: 0, by default, to orbit from
    /// where it spawned.
    /// </summary>
    public float Radius { get; set; }

    /// <summary>
    /// Degrees a second it swings about the world's up through the origin by itself once <see cref="Radius"/> out,
    /// keeping its height and distance and turning to look at the origin, until it is flown: 0, by default, for none.
    /// </summary>
    public float Orbit { get; set; }

    /// <summary>
    /// Starts looking the way its entity spawned facing.
    /// </summary>
    public void Start()
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, _world.Get<Transform>(owner).Rotation);
        _yaw = MathF.Atan2(forward.X, forward.Z);
        _pitch = MathF.Atan2(-forward.Y, new Vector2(forward.X, forward.Z).Length());
    }

    public void Update(in Frame frame)
    {
        Vector2 mouse = _input[Control.MouseRight].IsDown
            ? new Vector2(_input[Control.MouseMotionX].Value, _input[Control.MouseMotionY].Value) * MouseTurn
            : Vector2.Zero;
        Vector2 turn = mouse + (new Vector2(_input[Control.GamepadRightX].Value, -_input[Control.GamepadRightY].Value)
            * (StickTurn * frame.DeltaSeconds));
        float right = ReadAxis(Control.KeyD, Control.KeyA) + _input[Control.GamepadLeftX].Value;
        float forward = ReadAxis(Control.KeyW, Control.KeyS) + _input[Control.GamepadLeftY].Value;
        float up = ReadAxis(Control.KeyE, Control.KeyQ)
            + ReadAxis(Control.GamepadRightTrigger, Control.GamepadLeftTrigger);

        // Out from the middle, then around it, turning to look at it no faster than TurnToMiddle.
        ref Transform transform = ref _world.Get<Transform>(owner);
        _isFlyingItself &= turn == Vector2.Zero && right == 0 && forward == 0 && up == 0;
        Vector3 at = transform.Position;
        float fromMiddle = MathF.Sqrt((at.X * at.X) + (at.Z * at.Z));
        bool isOrbiting = _isFlyingItself && Orbit != 0 && fromMiddle >= Radius;
        float cruise = _isFlyingItself && fromMiddle < Radius ? Cruise * frame.DeltaSeconds : 0;
        if (isOrbiting)
        {
            at = Vector3.Transform(
                at,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, Orbit * frame.DeltaSeconds * (MathF.PI / 180)));
            float most = TurnToMiddle * frame.DeltaSeconds;
            turn.X = Math.Clamp(MathF.IEEERemainder(MathF.Atan2(-at.X, -at.Z) - _yaw, MathF.Tau), -most, most);
            turn.Y = Math.Clamp(MathF.Atan2(at.Y, fromMiddle) - _pitch, -most, most);
        }

        _yaw += turn.X;
        _pitch = Math.Clamp(_pitch + turn.Y, -MaxPitch, MaxPitch);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0);

        // Along where it looks, and up and down the world's up; no faster for two keys, or a key and a stick, at once.
        Vector3 move = Vector3.Transform(new Vector3(right, 0, forward), rotation) + (Vector3.UnitY * up);
        if (move.LengthSquared() > 1) move = Vector3.Normalize(move);

        bool isFast = _input[Control.KeyLeftShift].IsDown || _input[Control.GamepadLeftShoulder].IsDown;
        transform.Rotation = rotation;
        transform.Position = at + (move * (Speed * (isFast ? FastFactor : 1) * frame.DeltaSeconds))
            + (Vector3.Transform(Vector3.UnitZ, rotation) * cruise);
    }

    /// <summary>
    /// One control's value less another's: -1 to 1 for a pair of keys.
    /// </summary>
    private float ReadAxis(Control positive, Control negative)
    {
        return _input[positive].Value - _input[negative].Value;
    }
}

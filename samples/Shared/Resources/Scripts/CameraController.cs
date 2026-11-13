using System.Numerics;
using Magic.Assets;
using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Input;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Moves its camera with the keyboard and turns it with the mouse. At the first input, it takes the
/// <see cref="Spin"/> off its entity.
/// </summary>
/// <remarks>
/// W, A, S and D move the camera, and Left Shift moves it faster. Mouse motion with the right button down turns it.
/// </remarks>
public sealed class CameraController(Entity owner, World world, IInput input, AssetManager assets) : IBehavior
{
    private Handle<Script> _spin;

    /// <summary>
    /// How fast the camera moves, in metres a second.
    /// </summary>
    public float MetresPerSec { get; set; } = 5;

    /// <summary>
    /// How many times faster the camera moves while Left Shift is down.
    /// </summary>
    public float FastFactor { get; set; } = 5;

    /// <summary>
    /// How far the camera turns for each pixel that the mouse moves.
    /// </summary>
    public float DegreesPerPixel { get; set; } = 0.15f;

    public void Start() => _spin = assets.Find<Script>("Scripts/Spin.cs");

    public void Update(in Frame frame)
    {
        // Right, up and forward, in the space of the camera.
        Vector3 move = new(GetAxis(Control.KeyD, Control.KeyA), 0, GetAxis(Control.KeyW, Control.KeyS));
        Vector2 look = input[Control.MouseRight].IsDown
            ? new Vector2(input[Control.MouseMotionX].Value, input[Control.MouseMotionY].Value)
            : Vector2.Zero;
        if (move == Vector3.Zero && look == Vector2.Zero)
            return;

        if (world.Get<ScriptContainer>(owner).Has(_spin))
            world.Mutate<ScriptContainer>(owner).Remove(_spin);

        // Yaw turns about the world's up, and pitch about the camera's own right, so the camera never rolls.
        ref Transform transform = ref world.Mutate<Transform>(owner);
        float radiansPerPixel = DegreesPerPixel * MathF.PI / 180;
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, look.X * radiansPerPixel);
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, look.Y * radiansPerPixel);
        transform.Rotation = Quaternion.Normalize(
            Quaternion.Concatenate(Quaternion.Concatenate(pitch, transform.Rotation), yaw)
        );

        float speed = MetresPerSec * (input[Control.KeyLeftShift].IsDown ? FastFactor : 1);
        transform.Position += Vector3.Transform(move, transform.Rotation) * speed * (float)frame.AppDeltaSec;
    }

    private float GetAxis(Control positive, Control negative) =>
        (input[positive].IsDown ? 1 : 0) - (input[negative].IsDown ? 1 : 0);
}

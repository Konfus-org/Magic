using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Turns its entity about the world's up to face the player's camera, no faster than <see cref="DegreesPerSecond"/>,
/// so it swings round after the camera rather than snapping to it: a behaviour reading another entity's components.
/// <c>{ "id": 3441, "degreesPerSecond": 90 }</c>.
/// </summary>
internal sealed class Turret(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Which way it faces, a turn about the world's up from +Z.
    /// </summary>
    private float _yaw;

    public float DegreesPerSecond { get; set; } = 90;

    public void Start()
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, world.Get<Transform>(owner).Rotation);
        _yaw = MathF.Atan2(forward.X, forward.Z);
    }

    public void Update(in Frame frame)
    {
        if (!PlayerCamera.TryGetPosition(world, out Vector3 player)) return;

        // Toward the camera the short way round, no more than the rate allows this frame.
        ref Transform transform = ref world.Get<Transform>(owner);
        Vector3 toward = player - transform.Position;
        float wanted = MathF.Atan2(toward.X, toward.Z);
        float most = DegreesPerSecond * (MathF.PI / 180) * frame.WorldDeltaSec;
        _yaw += Math.Clamp(MathF.IEEERemainder(wanted - _yaw, MathF.Tau), -most, most);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw);
    }
}

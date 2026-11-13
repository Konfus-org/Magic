using System.Numerics;
using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Turns its entity in place at a steady rate, in the time of the world, so it stands still while the world is paused.
/// </summary>
public sealed class Spin(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Degrees a second. A positive value turns as a left-handed rotation about <see cref="Axis"/> does.
    /// </summary>
    public float DegreesPerSec { get; set; } = 6;

    /// <summary>
    /// What it turns about, in the space of its transform. It does not need unit length.
    /// </summary>
    public Vector3 Axis { get; set; } = Vector3.UnitY;

    public void Update(in Frame frame)
    {
        // A paused world, or an axis of no length, leaves the entity unchanged, so its renderer uploads nothing.
        float length = Axis.Length();
        if (frame.SimDeltaSec <= 0 || length <= 0)
            return;

        float angle = DegreesPerSec * (float)frame.SimDeltaSec * MathF.PI / 180;
        ref Transform transform = ref world.Mutate<Transform>(owner);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Axis / length, angle);
        transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(transform.Rotation, turn));
    }
}

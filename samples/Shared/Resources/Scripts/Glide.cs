using System.Numerics;
using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Moves its entity along its own right axis at a steady rate, in the time of the world, so it stands still while the
/// world is paused.
/// </summary>
public sealed class Glide(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Metres a second. A positive value moves the entity to its own right.
    /// </summary>
    public float MetresPerSec { get; set; } = 1;

    public void Update(in Frame frame)
    {
        // A paused world, or no speed, leaves the entity unchanged, so its renderer uploads nothing.
        if (frame.SimDeltaSec <= 0 || MetresPerSec == 0)
            return;

        ref Transform transform = ref world.Mutate<Transform>(owner);
        Vector3 right = Vector3.Transform(Vector3.UnitX, transform.Rotation);
        transform.Position += right * (MetresPerSec * (float)frame.SimDeltaSec);
    }
}

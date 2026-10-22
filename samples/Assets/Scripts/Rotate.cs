using System.Numerics;
using Magic.Attributes;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Turns its entity about the world's up at a steady rate, even while a domain loads in place of the world: the samples'
/// loading domain wheels its tilted ring round with it, where a system such as <see cref="SpinSystem"/> waits until the
/// world is there. About the world's up rather than its own, as a ring turned about its own axis looks still. A prefab
/// attaches it as <c>{ "id": 3432, "degreesPerSecond": 90 }</c>.
/// </summary>
[Impatient]
internal sealed class Rotate(Entity owner, World world) : IBehavior
{
    public float DegreesPerSecond { get; set; } = 45;

    public void Update(in Frame frame)
    {
        // This frame's turn about the world's up, after the rotation it has, normalised so rounding never builds up.
        ref Transform transform = ref world.Get<Transform>(owner);
        float radians = DegreesPerSecond * frame.DeltaSeconds * (MathF.PI / 180);
        transform.Rotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians) * transform.Rotation);
    }
}

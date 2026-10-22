using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Samples.Assets.Components;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Turns every entity with a <see cref="Spin"/> by its rate each frame, about its axis, on the ECS's threads: a script,
/// a system that a prefab attaches to an entity of its own, <c>{ "id": 3313 }</c>, so it runs while that entity's
/// domain is open.
/// </summary>
internal sealed class SpinSystem : IParallelSystemScript<Transform, Spin>
{
    public void Run(in Frame frame, EcsChunk<Transform, Spin> chunk)
    {
        (_, Span<Transform> transforms, Span<Spin> spins) = chunk;
        for (int i = 0; i < transforms.Length; i++)
        {
            // This frame's turn about its axis, the world's up when it has none, after the rotation it has, normalised
            // so rounding never builds up.
            Vector3 axis = spins[i].Axis == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(spins[i].Axis);
            float radians = spins[i].DegreesPerSecond * frame.DeltaSeconds * (MathF.PI / 180);
            transforms[i].Rotation = Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(axis, radians) * transforms[i].Rotation);
        }
    }
}

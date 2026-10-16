using System.Numerics;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Turns every entity with a <see cref="Spin"/> by its rate each frame, on the ECS's threads: a script, a system that a
/// prefab attaches to an entity of its own, <c>{ "id": 3313 }</c>, so it runs while that entity's domain is open.
/// </summary>
internal sealed class SpinSystem : IParallelSystemScript<Transform, Spin>
{
    public void Run(in Frame frame, EcsChunk<Transform, Spin> chunk)
    {
        (_, Span<Transform> transforms, Span<Spin> spins) = chunk;
        for (int i = 0; i < transforms.Length; i++)
        {
            float radians = spins[i].DegreesPerSecond * frame.DeltaSeconds * (MathF.PI / 180);
            transforms[i].Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians) * transforms[i].Rotation);
        }
    }
}

/// <summary>
/// Turns its entity about the world's up axis, as <see cref="SpinSystem"/> runs: a component, which a prefab names as
/// <c>"Spin": { "degreesPerSecond": 45 }</c>.
/// </summary>
internal record struct Spin : IComponent
{
    public float DegreesPerSecond;
}
